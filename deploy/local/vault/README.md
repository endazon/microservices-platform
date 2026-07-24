# 経路B ローカル Vault + External Secrets（opt-in）

> 起点: [ADR-0006](../../../docs/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST #24）

経路B（k8s）で **Vault dev モード**と External Secrets Operator の `ClusterSecretStore`（`vault-backend`）を立てる
**opt-in オーバーレイ**。AST chart 側の `ExternalSecret`（`ast-secrets` / `moomoo-*`・opt-in）がこのストアを
参照して Vault dev から同期できる状態を作る。

> ⚠️ **dev 専用・本番の Vault 化充足ではない**。Vault dev はインメモリ・単一 Pod・unseal 不要で、
> 再起動で中身が消える。本番は unseal / 監査 / HA / ローテーションを要する（Tier 3）。
> **平文の秘密（root トークン・API 鍵）をコミットしない。** root トークンは Secret `vault-dev-token`
> （dev 既定 or `VAULT_DEV_ROOT_TOKEN` 環境変数）から注入する。

## 構成

| ファイル | 役割 |
| --- | --- |
| `vault-dev.yaml` | Vault dev サーバ（Deployment/Service・`platform-infra`） |
| `clustersecretstore.yaml` | ESO `ClusterSecretStore` `vault-backend`（KV v2 `secret/`・**kubernetes 認証**・IADR-0096） |
| `oidc/` | **Keycloak OIDC(SSO) 連携**（IADR-0094・#353）: `bootstrap.sh`＋policy HCL＋手順。UI/CLI を Keycloak でログイン（`vault.localhost:50000`）。[oidc/README](oidc/README.md) |
| `eso/` | **Vault＋ESO で secret を Pod へ自動供給**（IADR-0096・#310）: `ESO=1` で ESO 導入＋k8s auth＋ExternalSecret。[eso/README](eso/README.md) |

## 適用（opt-in）

```sh
# 1) External Secrets Operator（CRD・一度だけ）
helm repo add external-secrets https://charts.external-secrets.io
helm install external-secrets external-secrets/external-secrets -n external-secrets --create-namespace

# 2) dev root トークン Secret（dev 既定 or env 上書き・k8s-local-up.sh が実施）
kubectl -n platform-infra create secret generic vault-dev-token \
  --from-literal=token="${VAULT_DEV_ROOT_TOKEN:-devroot}" --dry-run=client -o yaml | kubectl apply -f -

# 3) Vault dev + ClusterSecretStore
kubectl apply -k deploy/local/vault

# 4) 鍵を投入（例・実値は端末外に出さない・KV v2 は secret/ 配下）
kubectl -n platform-infra exec deploy/vault -- sh -lc \
  'VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN=$VAULT_DEV_ROOT_TOKEN_ID vault kv put secret/ai-stock-trading/app-secrets finnhub-api-key=...'
```

`scripts/k8s-local-up.sh` は `VAULT=1` で 2〜3 を実施する。**IADR-0096 (#310)**: `VAULT=1 ESO=1` で ESO 本体 install＋
Vault k8s auth＋ExternalSecret 供給まで自動化する（[eso/README](eso/README.md)）。

## AST 側の有効化

AST chart で `externalSecrets.enabled=true` ＋（API 鍵なら）`externalSecrets.appSecrets.enabled=true` を設定すると、
`vault-backend` を参照して Vault dev から同期する（手順は ai-stock-trading `docs/operations/vault-secrets-runbook.md`）。

## Tier 境界

Vault 本番運用（unseal/監査/HA/ローテーション）・[実弾解禁前提としての Vault 化実充足]は **Tier 3**（対象外）。
