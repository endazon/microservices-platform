# ローカル k8s(k3d) dev 環境（MSP + AST 連結）

> 起点: [IADR-0066](../../docs/adr/IADR-0066_local-k8s-dev-environment.md) /
> 作業仕様書 [`docs/specs/20260713_issue-266_local-k8s-dev-env.md`](../../docs/specs/20260713_issue-266_local-k8s-dev-env.md) /
> Issue #266（MSP）・ai-stock-trading#122（AST chart）・#121（K8s CronJob）

本ディレクトリは **dev 専用**の資産である。本番像（[`deploy/helm`](../helm) 本体・
[`deploy/docker-compose.yml`](../docker-compose.yml)・[`deploy/argocd`](../argocd)）は変更しない。

## 何を作るか

```
[k3d cluster: msp-ast-dev]
  ns platform-infra          postgres / rabbitmq / redis / keycloak / qdrant / otel-collector   ← deploy/local/infra
  ns microservices-platform  既存 Helm chart（values-local: mesh/NP/HPA off, registry=local）
                             + ExternalName エイリアス（素のサービス名 → platform-infra）
  ns ai-stock-trading        AST chart（ai-stock-trading#122 で追加）
```

MSP chart はインフラを**デプロイせず**素のサービス名（`postgres` 等）で参照するため、infra を
`platform-infra` に置き、各アプリ namespace に ExternalName エイリアスを張って名前解決させる。

## 必要ツール（Windows）

k8s ランタイムは **2 択**。スクリプトは `nerdctl`/`k3d` の有無で自動判定する（`K8S_LOCAL_RUNTIME=rancher|k3d` で明示指定可）。

**A. Rancher Desktop（推奨・内蔵 k3s）** — Docker Desktop も k3d も不要。
- Preferences → **Container Engine = containerd**、**Kubernetes = 有効**。
- 同梱の `kubectl` / `nerdctl` / `helm` を使う（`nerdctl --namespace k8s.io build` で k3s に直接供給）。
- kubectl context を `rancher-desktop` にしておく。

**B. Docker Desktop + k3d**
```powershell
winget install Docker.DockerDesktop   # WSL2 backend・メモリ 8GB+ 推奨
winget install Kubernetes.kubectl
winget install k3d                    # 無ければ choco install k3d
winget install Helm.Helm
```

> どちらも実体は k3s（ADR-0008）。A は内蔵 k3s をそのまま、B は k3d が k3s-in-docker を作る。

## 起動（Git Bash 推奨。1 コマンド）

```bash
bash scripts/k8s-local-up.sh          # クラスタ作成→build/import→secret→infra→MSP chart→alias
kubectl get pods -A
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/health
```

破棄:

```bash
bash scripts/k8s-local-down.sh
```

### opt-in オーバーレイ（可観測性 / Vault / GitOps・AST #24 / IADR-0077）

既定は**無効**（env 未設定で従来どおり）。env ゲートで追加のみ有効化する（既存ステップは不変）。

```bash
OBSERVABILITY=1 bash scripts/k8s-local-up.sh   # Prometheus/Loki/Tempo/Grafana + collector forwarding
VAULT=1         bash scripts/k8s-local-up.sh   # Vault dev + ClusterSecretStore(vault-backend)（要 ESO CRD）
ARGOCD=1        bash scripts/k8s-local-up.sh   # ArgoCD install + Application 適用（MSP/AST）
```

- [`deploy/local/observability/README.md`](observability/README.md) — 可観測性スタック（既定 debug-only を維持）
- [`deploy/local/vault/README.md`](vault/README.md) — Vault dev + External Secrets（**dev 専用・平文秘密なし**）
- [`deploy/local/argocd/README.md`](argocd/README.md) — GitOps ブートストラップ
- Hetzner 実 stand-up・本番 NFR は **Tier 3**（対象外）。

## 機密情報（fail-safe 既定）

`k8s-local-up.sh` は未設定なら **dev 既定 / 空（no-op）** で Secret を作成する。実接続を有効化する場合のみ
環境変数で上書きする（値は Git に載せない）。

| 環境変数 | 生成 Secret / キー | 既定 | 用途 |
| --- | --- | --- | --- |
| `PG_PASSWORD` | `platform-infra/postgres.password` | `postgres` | Postgres 管理 |
| `RABBITMQ_PASSWORD` | `platform-infra/rabbitmq.password` | `guest` | RabbitMQ |
| `KEYCLOAK_ADMIN_PASSWORD` | `platform-infra/keycloak-admin.password` | `admin` | Keycloak 管理 |
| `MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY` | `microservices-platform/minio-credentials` | `minioadmin` | MinIO（chart 参照） |
| `WIKIJS_DB_PASSWORD` | `microservices-platform/wikijs-db.password` | `kp` | Wiki.js DB |
| `WIKIJS_SYNC_APIKEY` | `microservices-platform/wikijs-sync.apiKey` | 空 | WikiService→Wiki.js 同期 |
| `ANTHROPIC_API_KEY` | `microservices-platform/llm-provider-credentials` | 空=呼ばない | MSP LLM Gateway（values-local が `Llm__ApiKey` へ配線） |

> `llm-provider-credentials` は values-local の `services.llmgateway.extraEnv` で LlmGateway の
> `Llm__ApiKey` に注入される（本ローカル環境のみ）。本番 chart（`deploy/helm` の `values.yaml`）は
> この Secret（`deploy/bootstrap/secret-templates.example.yaml` 由来）を未参照であり、本番側の配線は別課題。

AST 側の機密（`ANTHROPIC_API_KEY` / Finnhub / Discord / `Broker__Provider=paper`）は
ai-stock-trading#122 の chart で同様に fail-safe 既定で注入する。

## dev ログインユーザー（realm import・本番流用禁止）

realm import（`deploy/keycloak/microservices-platform-realm.json`）に含まれる開発専用ユーザーで
ログインできる（詳細は [`docs/security/security.md`](../../docs/security/security.md) の
「開発専用（dev-only）の平文認証情報」を参照）。

| ユーザー / パスワード | ロール・属性 | 用途 |
| --- | --- | --- |
| `developer` / `developer` | `platform-admin`+`platform-operator`+`wiki-editor`、clearance=`restricted` | 全機能を 1 アカウントで疎通確認する dev 用スーパーユーザー |
| `poc-user` / `poc-password` | ロール無し・ABAC 属性のみ（clearance=`internal`） | ABAC 属性ユーザーの検証 |
| `poc-operator` / `poc-operator-password` | `platform-operator` のみ | 運用者ロールの検証 |

> ロール別の挙動差分（権限分離）を確認したい場合は `developer` ではなく `poc-*` を使うこと。

## AST 統合スタック疎通（エッジ /bff・ブラウザ OIDC・Issue #284）

> 起点: [IADR-0076](../../docs/adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) /
> 作業仕様書 [`docs/specs/20260719_issue-284-live-integration-wiring.md`](../../docs/specs/20260719_issue-284-live-integration-wiring.md)

### AST 3 サービスの有効化（自動）

`values-local.yaml` は AST の 3 画面系サービス（`configuration` / `risk-management` / `market-monitor`）を
**経路B で自動有効化**する（本番像 `values.yaml` は fail-safe の disabled のまま不変）。接続情報は
`values-local` の `extraEnv` が注入する（DB は経路B postgres の owner=ai に合わせ `ai/ai`、RabbitMQ は `guest/guest`）。
イメージは `k8s-local-images.sh` が MAPPING から 3 サービスとも build/import する。BFF の `/bff/assumptions`・
`/bff/risk-controls/*`・`/bff/monitor/*` はこれらへプロキシする（#283/#287/#288）。

### エッジ /bff/* ルーティング

本番像は Istio `Gateway`/`VirtualService`（`edge.*`、`templates/edge.yaml`）で外部の `/bff/*` を `bff-service` へ
通す。経路B は Istio を導入しないため `values-local` で `edge.enabled=false`。経路B で `/bff/*` に到達するには
BFF を直接 port-forward する:

```bash
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/bff/...   （認証必須。匿名は 401）
```

### SPA(/settings) 到達（Issue #313 / IADR-0078）

`values-local` は `frontend.enabled=true` で SPA(frontend) を k8s に配信する（`k8s-local-images.sh` が
`k3d-local/microservices-platform/frontend` を build/import・#275 MAPPING 登録済み）。経路B は `edge.enabled=false`
（Istio 未導入）のため、SPA へはエッジではなく `frontend-service` を直接 port-forward して到達する:

```bash
kubectl -n microservices-platform port-forward svc/frontend-service 3100:8080
#   → http://localhost:3100/            （SPA。/settings=SC-01/02/03）
#   → http://localhost:3100/bff/...     （nginx が in-cluster bff-service:8080 へプロキシ。BFF port-forward 不要）
```

frontend pod の nginx が `/bff/*` を in-cluster の `bff-service:8080` へ内部プロキシするため、上の BFF port-forward
（5080）は SPA 経由では不要（`/bff` を直接叩いて確認したい場合のみ使う）。OIDC は下記 issuer 統一の**手順A**に
従う（browser も cluster も `http://keycloak:8080` を issuer として共有する。`values-local` は OIDC を上書きせず
base 既定 `http://keycloak:8080/realms/microservices-platform` のまま＝backend の `Auth__Authority` と一致）。

> 本番像は `edge.enabled=true` で Istio VirtualService の catch-all（`/bff`・`/realms` の後）が SPA を
> `frontend-service` へ流し、`allow-edge-ingress-to-frontend` NetworkPolicy が default-deny 下の到達を許可する。
> 実ブラウザでの `/settings` 実表示・OIDC 実ログインは稼働 k3d 依存（本 issue の live 分・#284 手順）。

### ブラウザ OIDC の issuer 統一（原則と 2 手順）

**原則**: ブラウザが受け取る token の `iss` と、サービス側の検証基準（`Auth__Authority`）が **同一 URL** で
なければならない。issuer は in-cluster 正準名 `http://keycloak:8080` に固定している（サービス間 JWT 用）。

- **手順A（推奨・realm/manifest 無改変）**: ブラウザに同じ in-cluster 名を解決させる。
  1. hosts に `127.0.0.1 keycloak` を追記（Windows: `C:\Windows\System32\drivers\etc\hosts`）。
  2. `kubectl -n platform-infra port-forward svc/keycloak 8080:8080`。
  3. これで browser も cluster も `http://keycloak:8080` を issuer として共有する。SPA は compose の frontend
     （`http://localhost:3100`・既存 `spa-web` origin）を使い、その `BFF_UPSTREAM` を上記 BFF port-forward
     （`http://localhost:5080`）へ、`OIDC_AUTHORITY` を `http://keycloak:8080/realms/microservices-platform` へ向ける。
  4. token 検証: 取得した access_token を base64url デコードし `iss` と `realm_access.roles`（`trading-owner`）を確認する。
- **手順B（単一エッジ host に集約する場合・任意）**: chart の `edge.oidc.enabled=true` で SPA/`/bff`/`/realms` を
  同一エッジ host に集約できる（`edge.oidc.host/port` で Keycloak を指す）。この場合のみ運用者が (i) その host を
  `spa-web` の redirectUris/webOrigins へ追記、(ii) `global.auth.authority` を同 host へ上書き、(iii) in-cluster から
  同 host を解決させる（CoreDNS 追記 or backend の metadata/issuer 分離）。(iii) は稼働環境依存＝live。

> 実ブラウザログイン end-to-end・Playwright E2E・Pod 実起動ヘルス緑は稼働 k3d 依存（本 issue の live 分・#284）。

## 既知の制約

- **観測 UI は非同梱**: otel-collector は dev では `debug` エクスポータのみ（Prometheus/Tempo/Loki/Grafana は
  立てない）。UI が要るなら compose（`deploy/docker-compose.yml`）を併用する。
- **永続化なし**: infra は emptyDir（Pod 再起動で再 init）。dev 用途の割り切り。
- **Istio/mTLS/NetworkPolicy/HPA/エッジ Gateway は無効**（values-local。`edge.enabled=false`）。本番像（STRICT mTLS・
  エッジ `/bff/*` ルーティング等）は不変。経路B の `/bff` 到達は BFF の port-forward で代替する（上記手順）。

## 手動でステップ実行する場合

```bash
# 事前に infra secrets と realm ConfigMap を作成（k8s-local-up.sh が自動化する部分）
kubectl create namespace platform-infra
kubectl create secret generic postgres -n platform-infra --from-literal=password=postgres
kubectl create secret generic rabbitmq -n platform-infra --from-literal=password=guest
kubectl create secret generic keycloak-admin -n platform-infra --from-literal=password=admin
kubectl create configmap keycloak-realms -n platform-infra \
  --from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json

kubectl apply -k deploy/local/infra                                  # infra
helm upgrade --install msp deploy/helm/microservices-platform \
  -n microservices-platform --create-namespace -f deploy/local/values-local.yaml
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml
```
