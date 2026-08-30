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

### 🔴 現時点で STRICT は成立しない（2026-08-30 実測）

`kube-system` の **Traefik にサイドカーが無い**ため、名前空間全体へ STRICT を掛けると
**エッジからの平文が拒否され、SPA / BFF が 502 になる**。

| `mtls.mode` | `http://localhost/` | `https://localhost/` |
| --- | --- | --- |
| PERMISSIVE | 200 | 200 |
| **STRICT** | 🔴 **502** | 🔴 **502** |

**メッシュ内は STRICT でも健全である**（28 Deployment available・アプリログのエラー 0 件）。
**壊れるのは入口だけ。**

したがって **`ADR-0021`（エッジ＝Istio Ingress Gateway）は STRICT の前提**であり、
経路B のエッジを Traefik にした構成（`IADR-0091`）とは両立しない。
**エッジをメッシュへ入れるまで STRICT へ上げないこと。** 移行は #458 が引き受ける。

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

```sh
# 各ワークロードの mTLS 状態（STRICT であること）
istioctl authn tls-check <pod>.microservices-platform

# サイドカーのリスナ設定に平文（PERMISSIVE/DISABLE）が無いこと
istioctl proxy-config listener <pod> -n microservices-platform

# PeerAuthentication が STRICT で適用されていること
kubectl get peerauthentication -n microservices-platform -o yaml
```

受け入れ基準「平文の内部通信が存在しない」は、`istioctl authn tls-check` が全エッジで
`STRICT` を報告し、サイドカー未注入クライアントからの平文到達が拒否されることで確認する。
