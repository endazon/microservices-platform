# ローカル k8s(k3d) dev 環境（MSP + AST 連結）

> 起点: [IADR-0066](../../docs/adr/IADR-0066_local-k8s-dev-environment.md) /
> 作業仕様書 [`docs/specs/20260713_issue-266_local-k8s-dev-env.md`](../../docs/specs/20260713_issue-266_local-k8s-dev-env.md) /
> Issue #266（MSP）・ai-stock-trading#122（AST chart）・#121（K8s CronJob）

本ディレクトリは **dev 専用**の資産である。本番像（[`deploy/helm`](../helm) 本体・
[`deploy/docker-compose.yml`](../docker-compose.yml)・[`deploy/argocd`](../argocd)）は変更しない。

## 何を作るか

```
[k3d cluster: msp-ast-dev]
  ns platform-infra          postgres / rabbitmq / keycloak / qdrant / otel-collector   ← deploy/local/infra
  ns microservices-platform  既存 Helm chart（values-local: mesh/NP/HPA off, registry=local）
                             + ExternalName エイリアス（素のサービス名 → platform-infra）
  ns ai-stock-trading        AST chart（ai-stock-trading#122 で追加）
```

MSP chart はインフラを**デプロイせず**素のサービス名（`postgres` 等）で参照するため、infra を
`platform-infra` に置き、各アプリ namespace に ExternalName エイリアスを張って名前解決させる。

## 必要ツール（Windows）

```powershell
winget install Docker.DockerDesktop   # WSL2 backend・メモリ 8GB+ 推奨
winget install Kubernetes.kubectl
winget install k3d                    # 無ければ choco install k3d
winget install Helm.Helm
```

## 起動（Git Bash 推奨。1 コマンド）

```bash
scripts/k8s-local-up.sh               # クラスタ作成→build/import→secret→infra→MSP chart→alias
kubectl get pods -A
kubectl -n microservices-platform port-forward svc/bff-service 5080:8080
#   → http://localhost:5080/health
```

破棄:

```bash
scripts/k8s-local-down.sh
```

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
| `ANTHROPIC_API_KEY` | `microservices-platform/llm-provider-credentials` | 空=呼ばない | MSP LLM Gateway |

AST 側の機密（`ANTHROPIC_API_KEY` / Finnhub / Discord / `Broker__Provider=paper`）は
ai-stock-trading#122 の chart で同様に fail-safe 既定で注入する。

## 既知の制約

- **サービス間 JWT は成立、ブラウザ OIDC は要追加設定**: Keycloak issuer を in-cluster 正準名
  `http://keycloak:8080`（chart の `Auth__Authority` と一致）に固定している。ブラウザからの OIDC ログイン
  （Wiki.js 等）を使う場合は hostname/ingress を別途調整する（#121 の検証には不要）。
- **観測 UI は非同梱**: otel-collector は dev では `debug` エクスポータのみ（Prometheus/Tempo/Loki/Grafana は
  立てない）。UI が要るなら compose（`deploy/docker-compose.yml`）を併用する。
- **永続化なし**: infra は emptyDir（Pod 再起動で再 init）。dev 用途の割り切り。
- **Istio/mTLS/NetworkPolicy/HPA は無効**（values-local）。本番像（STRICT mTLS 等）は不変。

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
