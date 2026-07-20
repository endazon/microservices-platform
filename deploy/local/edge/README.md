# 経路B ローカルエッジ集約（opt-in・Traefik）

> 起点: [IADR-0091](../../../docs/adr/IADR-0091_local-edge-aggregation-traefik.md) /
> 作業仕様書 [`docs/specs/20260720_issue-356_local-edge-aggregation.md`](../../../docs/specs/20260720_issue-356_local-edge-aggregation.md) / Issue #356

経路B（k3d / Rancher Desktop 内蔵 k3s）で、**platform フロント（SPA/BFF）を 80/443**、**管理ツール群を単一ポート
50000** に集約する **opt-in オーバーレイ**。ローカルは Istio 未導入（`values-local` は `edge.enabled=false`）のため、
既に稼働している **k3s 内蔵 Traefik** をエッジに使う（prod の Istio `templates/edge.yaml` とは別実装）。

## 構成

| ファイル | 役割 |
| --- | --- |
| `traefik-entrypoint.yaml` | k3s Traefik に追加 entrypoint `admin:50000` を定義（`HelmChartConfig`） |
| `platform-frontend-ingress.yaml` | 80/443（web/websecure）: `/bff`→bff-service、catch-all→frontend-service |
| `admin-ingress-infra.yaml` | 50000（admin）: grafana/headlamp/vault/qdrant をホスト名ベースで公開（platform-infra） |
| `argocd-ingress.yaml` | 50000（admin）: argocd-server（argocd ns 存在時のみスクリプトが条件付き apply） |

## 有効化（opt-in・既定オフ）

```sh
LOCALEDGE=1 bash scripts/k8s-local-up.sh          # 必要に応じ OBSERVABILITY=1 HEADLAMP=1 VAULT=1 ARGOCD=1 を併記
```

`scripts/k8s-local-up.sh` は `LOCALEDGE=1` のとき、(1) k3d cluster を **80/443/50000 公開で作成**し、(2) 本オーバーレイを
適用する。**既定（未設定）は現行の 8080/8443・overlay 不適用でバイト等価**（後方互換・fail-safe）。

### k3d はポートが cluster 作成時固定 → 既存クラスタは再作成が必要（ユーザー実行）

ポート公開は k3d の cluster **作成時**にしか設定できない（後付け不可）。既存クラスタに `LOCALEDGE` を効かせるには
**削除→再作成**する（破壊操作のため利用者が実行する）:

```sh
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
```

### Rancher Desktop（内蔵 k3s）の差分

Rancher Desktop は k3d の `-p ...@loadbalancer` を使わず、内蔵 k3s の LoadBalancer サービスを **localhost へ自動公開**
する。`traefik-entrypoint.yaml` で admin:50000 を足せば Rancher が localhost:50000 を公開し、80/443 も Traefik LB
経由で公開される。**cluster 再作成は不要**で、overlay 適用のみでよい:

```sh
kubectl apply -k deploy/local/edge
# argocd を併用しているなら:
kubectl get ns argocd >/dev/null 2>&1 && kubectl apply -f deploy/local/edge/argocd-ingress.yaml
```

## アクセス

- **platform フロント**: `http://localhost/`（SPA）・`http://localhost/bff/...`（BFF）。`https://localhost/` は
  Traefik 既定の**自己署名証明書**（ブラウザ警告が出る。実 TLS 証明書は本オーバーレイのスコープ外）。
- **管理ツール（50000・ホスト名ベース）**:
  - `http://grafana.localhost:50000`（OBSERVABILITY=1）
  - `http://headlamp.localhost:50000`（HEADLAMP=1）
  - `http://vault.localhost:50000`（VAULT=1）
  - `http://qdrant.localhost:50000`（dashboard は `/dashboard`。**SSO 非対応＝認証なし**・閉域前提）
  - `http://argocd.localhost:50000`（ARGOCD=1。argocd-server の `server.insecure` は ArgoCD OIDC 実装 #353 で設定）

### ホスト名解決の注意（`*.localhost` / CLI）

- **ブラウザ**（Chrome/Edge/Firefox/Safari）は `*.localhost` を 127.0.0.1 に自動解決するため、UI アクセスは追加設定不要。
- **CLI**（`argocd` / `vault` 等）や一部 OS リゾルバは `*.localhost` を解決しないことがある（特に Windows）。その場合は
  hosts に追記するか、ワイルドカード DNS を使う:
  - hosts（`C:\Windows\System32\drivers\etc\hosts` / `/etc/hosts`）: `127.0.0.1 grafana.localhost argocd.localhost vault.localhost headlamp.localhost qdrant.localhost`
  - もしくは `grafana.127.0.0.1.nip.io:50000` 等の `*.nip.io` / `*.sslip.io`（hosts 編集不要・ワイルドカード解決）。

## OIDC（redirect の追記は #355 マージ後）

issuer は最小案（`http://keycloak:8080`・[README 手順A](../README.md)）を維持し、ツール UI のみ 50000 に集約する。
集約後 URL への **redirectUris 追加**（grafana `…/grafana.localhost:50000/login/generic_oauth`、headlamp
`…headlamp.localhost:50000/*`）と Grafana の `GF_SERVER_ROOT_URL` 設定は、`realm.json`・`grafana.yaml` を触るため
**#355（Grafana OIDC）マージ後の追従 PR（PR-2）**で行う（既存 port-forward 用 URL を残す＝後方互換）。それまでは
新ホストからの OIDC ログインは未成立で、従来の port-forward + 既存 redirect でログインできる（フォールバック維持）。
これから足す ArgoCD/Vault の OIDC client は最初から 50000 URL で登録する。

## 切り戻し

```sh
kubectl delete -k deploy/local/edge
kubectl -n kube-system delete helmchartconfig traefik   # admin:50000 を撤去（Traefik が既定 values で再適用される）
```

k3d のポートを元（8080/8443）へ戻すにはクラスタ再作成（`LOCALEDGE` 未設定で `k8s-local-up.sh`）。

## Tier 境界

本オーバーレイはローカル検証用。実 TLS 証明書・本番相当のエッジ（Istio）・稼働率は **Tier 3**（対象外）。
