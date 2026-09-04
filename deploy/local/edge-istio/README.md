# 経路B エッジの Istio Ingress Gateway 化（opt-in・`ISTIO=1` ＋ `LOCALEDGE=1`）

> 起点: 計画 `ADR-0021`（エッジ＝Istio Ingress Gateway ＋ Caddy・`Accepted`）/ `ADR-0005`（メッシュ）/
> 実装 [IADR-0317](../../../.ai-context/adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md) /
> 作業仕様書 [`.ai-context/specs/20260830_issue-782_istio-ingressgateway-strict-mtls.md`](../../../.ai-context/specs/20260830_issue-782_istio-ingressgateway-strict-mtls.md) / Issue #782

## なぜ要るのか

**STRICT mTLS の前提だからである。** `kube-system` の Traefik は**メッシュの外**にあり、そこから
mesh 内の 4 Service（`frontend-service` / `bff-service` / `wiki-js` / `minio`）へ**平文で入っている**。
`PeerAuthentication` を STRICT にすると Envoy がその平文を拒否し、**入口だけが 502 になる**（実測）。

計画 `ADR-0021` はこの境界問題を理由に、入口を mesh ネイティブな Envoy にすると定めている。
本オーバーレイはそれを経路B で実装する（Traefik エッジ [`../edge/`](../edge/README.md) の**置き換え**）。

## 構成

| ファイル | 役割 |
| --- | --- |
| `traefik-service-off.yaml` | Traefik の LoadBalancer Service を落として **80/443/50000 の hostPort を明け渡す**（`HelmChartConfig`）。**kustomization には入れない**（Gateway より先に当てる必要があるため） |
| `gateway.yaml` | `msp-edge`（80 は 443 へリダイレクト／443 HTTPS）と `msp-admin-edge`（50000 HTTPS）。どちらも `credentialName: edge-tls` |
| `virtualservice-app.yaml` | 443: catch-all（`/bff`→bff-service、`/private-notes/sync/`→document-service、残り→frontend-service）と `keycloak.localhost` |
| `virtualservice-admin.yaml` | 50000: grafana / headlamp / vault / qdrant / minio / wiki / argocd の 7 host |
| `coredns-edge-hosts.yaml` | pod 側の `*.localhost` 解決先を `istio-ingressgateway.istio-system` へ差し替える |
| `tls/edge-certificate-istio.yaml` | `istio-system` の葉証明書 `edge-tls`（Gateway は同 namespace の Secret しか読めない）。**kustomization には入れない**（cert-manager の CRD 依存） |

istiod / ingressgateway の values は [`../../istio/`](../../istio/README.md)。

## 使い方

```sh
# クラスタ作成から通す（推奨）
ISTIO=1 LOCALEDGE=1 ./scripts/k8s-local-up.sh                        # PERMISSIVE
ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT ./scripts/k8s-local-up.sh # STRICT

# 既に立っているクラスタのエッジだけを移す
bash scripts/istio-edge-up.sh
ISTIO_MTLS_MODE=STRICT bash scripts/istio-edge-up.sh

# 🔴 切り戻し（1 コマンド・冪等）。**触る前に読むこと**
bash scripts/istio-edge-down.sh
```

🔴 **mTLS モードは helm を通してしか書かない**（#1159 / [`IADR-0374`](../../../.ai-context/adr/IADR-0374_mesh-mtls-single-writer-and-drift-gate.md)）。
`ISTIO_MTLS_MODE` は最終的に `scripts/lib/mesh-mtls-mode.sh` の `set_mesh_mtls_mode`
（＝ `helm upgrade --reuse-values --set mesh.mtlsMode=…`）に落ちる。
`kubectl patch` で直接書くと field manager が helm から奪われ、**以後の `helm upgrade` が
恒久的に失敗する**（復旧手順は `docs/operations/operations.md` の Runbook）。
`ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT` の up は、**[6/7] でいったん PERMISSIVE を宣言し、
入口を移した後に STRICT へ上げる**（段取りは `IADR-0307` 決定 4）。

## 🔴 順序が命である

k3s の ServiceLB（klipper）は **LoadBalancer Service ごとに hostPort を握る DaemonSet を作る**。
**80/443/50000 を 2 つの Service が同時に持てない。**

| | 順序 | 逆にすると |
| --- | --- | --- |
| up | ① Traefik を明け渡す → ② Gateway を立てる | svclb が bind に失敗し**どちらの入口も立たない** |
| down | ① mTLS を緩める → ② Gateway を撤去 → ③ Traefik を戻す | ①を飛ばすと**入口は戻るのに 502 のまま**／②③を逆にすると svclb が衝突する |

`scripts/k8s-local-up.test.js` がこの順序を静的に固定している（壊すと落ちる）。

## 3 ポートすべてを移す理由（「80/443 だけ」は成立しない）

`admin(50000)` には **mesh 内の 2 件**（`minio` / `wiki-js`）が載っている。50000 を Traefik に残すと
その 2 件が STRICT で落ちる。かといって 443 へ移すこともできない —— **7 つの OIDC クライアントの
redirect URI が `:50000` 付きで Keycloak に登録済み**である（`IADR-0092`〜`IADR-0095` / `IADR-0220`）。

## 既知の限界

- **エッジ宣言が 2 つある**（Traefik 用と Istio 用）。ルートを足すときは**両方を触る**。
  opt-in を保つ（既定はバイト等価）ための代償である。
- ~~**SPA 配信はまだ nginx である。**~~ **解消した**（#1135 / [IADR-0362](../../../.ai-context/adr/IADR-0362_spa-serving-caddy.md)）。
  `ADR-0021` が定める Caddy へ移送済みで、`ADR-0021` のエッジ構成（入口 ＝ Istio Ingress Gateway ＋
  SPA 配信 ＝ Caddy）は**両方揃った**。
- `istio-system` の Gateway / VirtualService は **ArgoCD の AppProject の対象外**である
  （`destinations` は `microservices-platform` namespace のみ）。本番像の Gateway は
  アプリチャートの `templates/edge.yaml` が持つ（そちらは whitelist 済み）。
