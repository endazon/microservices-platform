# Istio サービスメッシュ導入（STRICT mTLS）

> 起点: ADR-0005（サービスメッシュ / Istio / mTLS）
> 関連: IADR-0017（暫定: ネットワーク分離を第一防御）→ **IADR-0026（本 mTLS で Supersede）**
> 回帰テスト: `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/MeshMtlsTests.cs`

サービス間通信を **STRICT mTLS**（平文フォールバック無し）で暗号化・相互認証する。
mTLS を強制する宣言（`PeerAuthentication` / `DestinationRule`）は Helm チャート
（`deploy/helm/microservices-platform/templates/istio-mtls.yaml`）に含まれ、ArgoCD が同期する。

## 1. Istio 本体の導入

### 経路B（ローカル k3s）— `ISTIO=1` の opt-in（#782 で配線した）

```sh
ISTIO=1 ./scripts/k8s-local-up.sh
```

`scripts/k8s-local-up.sh` が次を **[6/7] の前に** 行う（CRD が無いまま helm upgrade すると apply が失敗するため）:

1. `istio/base`（CRD）と `istio/istiod`（コントロールプレーン）を Helm で導入する
   （版は `ISTIO_VERSION`。既定 `1.30.4`。values は [`istiod-values-local.yaml`](istiod-values-local.yaml)）
2. 4 つの CRD が `Established` になるまで待つ
3. `microservices-platform` へ `istio-injection=enabled` を貼る
   —— 🔴 **経路B は `namespace.create=false` なので Helm は Namespace を作らない。**
   チャートの `istioInjection` だけでは注入ラベルが誰にも適用されない
4. アプリチャートを `mesh.enabled=true` で適用する
5. `rollout restart deployment` —— **サイドカーは既存 Pod へ後から入らない**

🔴 **注入の確認は `initContainers` も見る。** Istio 1.30 は k8s のネイティブサイドカー
（`restartPolicy: Always` の initContainer）を使うため、**`spec.containers` だけを見ると
「注入 0 件」と誤答する**（2026-08-30 に実際に誤答した）。

```sh
kubectl -n microservices-platform get pods -o json |   jq -r '.items[] | .metadata.name + " " +
         (([.spec.containers[].name] + [(.spec.initContainers//[])[].name])
          | if index("istio-proxy") then "injected" else "NOT-INJECTED" end)'
```

**`istioctl` は要らない**（配布バイナリを増やさず、他の opt-in と同じ Helm 経路に揃える）。
検証コマンド（§4）だけは `istioctl` があると便利だが、`kubectl` でも代替できる。

### 🔴 mTLS モードは既定 PERMISSIVE で入る

```sh
ISTIO=1 ./scripts/k8s-local-up.sh                        # PERMISSIVE（既定）
ISTIO=1 ISTIO_MTLS_MODE=STRICT ./scripts/k8s-local-up.sh # STRICT へ移す
```

**いきなり STRICT にしてはならない。** サイドカーの入っていない `platform-infra`
（postgres / keycloak / rabbitmq / qdrant / redis / minio …）との通信と、注入前の Pod からの通信が
**同時に**壊れ、どちらが原因か切り分けられなくなる。段取りは
**注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT** である。

### 🔴 STRICT には **エッジがメッシュの中に居ること**が要る（#782 で解いた）

`kube-system` の **Traefik にはサイドカーが無い**。名前空間全体へ STRICT を掛けると
**エッジからの平文が拒否され、SPA / BFF が 502 になる**（2026-08-30 実測）。

| エッジ | `mtls.mode` | `http://localhost/` | `https://localhost/` |
| --- | --- | --- | --- |
| Traefik | PERMISSIVE | 301→200 | 200 |
| Traefik | **STRICT** | 🔴 **502** | 🔴 **502** |
| **Istio Ingress Gateway** | PERMISSIVE | 301→200 | 200 |
| **Istio Ingress Gateway** | **STRICT** | ✅ **301→200** | ✅ **200** |

**メッシュ内は Traefik のままでも STRICT で健全だった**（Deployment available・アプリログのエラー 0 件）。
**壊れていたのは入口だけ**であり、計画 `ADR-0021`（エッジ＝Istio Ingress Gateway）は
**STRICT にとって選択肢ではなく前提**である。

したがって経路B では **`ISTIO=1` と `LOCALEDGE=1` を併用してエッジを Envoy へ移してから** STRICT へ上げる。

```sh
ISTIO=1 LOCALEDGE=1 ./scripts/k8s-local-up.sh                        # PERMISSIVE で立てる
ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT ./scripts/k8s-local-up.sh # STRICT へ移す

bash scripts/istio-edge-up.sh     # 既に立っているクラスタのエッジだけを移す
bash scripts/istio-edge-down.sh   # 🔴 切り戻し（1 コマンド）。**触る前に読むこと**
```

エッジ資材は [`../local/edge-istio/`](../local/edge-istio/)（Gateway 2 本 ＋ VirtualService 9 本 ＋
`istio-system` の葉証明書 ＋ Traefik の Service を落とす `HelmChartConfig`）。
判断と実測は [`IADR-0312`](../../.ai-context/adr/IADR-0312_istio-ingressgateway-edge-and-strict-mtls.md)。

🔴 **`ISTIO=1` を `LOCALEDGE=1` 無しで使うとエッジは移らない**（port-forward のまま）。
その状態で STRICT へ上げると mesh へ入る経路が無くなる。スクリプトが警告を出す。

### 本番像

`values.yaml` は `mesh.enabled: true` / `mtlsMode: STRICT` / `namespace.create: true` が既定であり、
ArgoCD が同期する。**AppProject の `namespaceResourceWhitelist` に Istio の 4 種別
（PeerAuthentication / DestinationRule / Gateway / VirtualService）が載っていること**を前提とする
（#782 で 6 種別の欠落を是正した。[`../argocd/appproject.yaml`](../argocd/appproject.yaml)）。

## 2. STRICT mTLS の適用（Helm が宣言）

`mesh.enabled=true` / `mesh.mtlsMode=STRICT`（既定）で、以下がレンダリング・適用される:

- `PeerAuthentication microservices-platform-mtls`（`mtls.mode: STRICT`）
  — Namespace 内ワークロードが受け付ける接続を mTLS のみに限定。
- `DestinationRule microservices-platform-mtls`（`trafficPolicy.tls.mode: ISTIO_MUTUAL`）
  — 同 Namespace 宛の送信 TLS をメッシュ証明書での相互 TLS に固定。

> 実クラスタへの初回導入時は、一時的に `mesh.mtlsMode=PERMISSIVE` で移行を確認してから
> `STRICT` へ切り替える運用が安全（平文とmTLSを併存させて段階移行）。

## 3. Kiali（可観測性）

メッシュのトラフィック・mTLS 状態を可視化する Kiali を配備する。

```sh
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.24/samples/addons/kiali.yaml
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.24/samples/addons/prometheus.yaml
istioctl dashboard kiali
```

Kiali の Security バッジ（鍵アイコン）で、サービス間エッジが mTLS であることを確認する。

## 4. 検証（平文が存在しないこと）

**`istioctl` は前提にしない**（配布バイナリを増やさない方針・§1）。同じことを `kubectl` で測る。

```sh
# (1) PeerAuthentication が STRICT で、port 例外（portLevelMtls）の穴が無いこと
kubectl get peerauthentication -A -o json | jq -r '.items[] |
  "\(.metadata.namespace)/\(.metadata.name) mode=\(.spec.mtls.mode) ports=\(.spec.portLevelMtls // {} | tostring)"'

# (2) DestinationRule が ISTIO_MUTUAL であること（DISABLE が無いこと）
kubectl get destinationrule -A -o json | jq -r '.items[] |
  "\(.metadata.name) host=\(.spec.host) tls=\(.spec.trafficPolicy.tls.mode)"'

# (3) サイドカーの inbound リスナに平文の受け口が無いこと（istioctl proxy-config listener の代替）
POD=$(kubectl -n microservices-platform get pod -l app=bff-service -o jsonpath='{.items[0].metadata.name}')
kubectl -n microservices-platform exec "$POD" -c istio-proxy -- pilot-agent request GET 'config_dump?resource=dynamic_listeners&mask=active_state.listener' |
  jq -r '.configs[]? | select(.active_state.listener.name=="virtualInbound")
         | .active_state.listener.filter_chains[]
         | "\(.name // "-")  transport_socket=\(.transport_socket.name // "NONE")  match=\(.filter_chain_match|tostring)"'
```

🔴 **(3) は `transport_socket=NONE` の chain を 1 つ返すが、それは平文の受け口ではない。**
`virtualInbound-blackhole`（`destination_port: 15006`）は **Envoy 自身の inbound ポートへ
直接来た接続を捨てるための chain** である。見るべきは「アプリへ渡る chain が
すべて `transport_protocol: tls` を要求しているか」であって、chain の総数ではない。
**数だけ数えると「平文が 1 件残っている」と誤読する。**

```sh
# (4) 変異試験 — メッシュ外の Pod から平文で入って**拒否されること**（宣言だけを信用しない）
kubectl -n platform-infra run mtls-probe --rm -i --restart=Never --image=curlimages/curl:8.11.1 --command -- curl -sS --max-time 10 http://frontend-service.microservices-platform.svc.cluster.local:8080/
# STRICT   -> curl: (56) Recv failure: Connection reset by peer
# PERMISSIVE -> HTTP 200
```

```sh
# (5) エッジ疎通。🔴 -k を使わない（証明書が壊れていても気付けなくなる。#1074 の事故）
kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > /tmp/root-ca.pem
curl --cacert /tmp/root-ca.pem --ssl-revoke-best-effort https://localhost/ -o /dev/null -w '%{http_code}\n'
```

Windows の `curl`（schannel）は私有 CA で失効確認が `unknown` になり接続自体が落ちる。
`--ssl-revoke-best-effort` を足す —— **失効確認だけ**が best-effort になり、
チェーン検証とホスト名照合は有効なままである（`-k` とは別物）。

受け入れ基準「平文の内部通信が存在しない」は、(1)〜(3) が穴の不在を示し、
**(4) が実際に拒否されること**で確認する。**(4) の無い (1)〜(3) は宣言の朗読でしかない。**
