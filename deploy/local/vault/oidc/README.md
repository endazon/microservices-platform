# dev Vault の Keycloak OIDC(SSO)（IADR-0094・#353）

> 起点: [IADR-0094](../../../../docs/adr/IADR-0094_vault-keycloak-oidc.md) /
> 作業仕様書 [`docs/specs/20260721_issue-353_vault-keycloak-oidc.md`](../../../../docs/specs/20260721_issue-353_vault-keycloak-oidc.md)

経路B の dev Vault（`VAULT=1` の opt-in・`-dev`・インメモリ・unseal 不要）を Keycloak OIDC でログインできるようにする。
Vault の OIDC 設定は **runtime**（`vault write auth/oidc/*`）のため、`vault-dev.yaml` は無改変で **bootstrap 手順**で入れる
（realm import や MinIO の `mc` と同型）。root トークンは break-glass として残る。edge の `vault.localhost:50000` Ingress は
#357 で追加済み（本 PR では無改変）。

## client secret（自動）

`k8s-local-up.sh` は `VAULT=1` 時に Secret `vault-oidc`（dev 既定 `vault-dev-secret-change-me`・`VAULT_OIDC_CLIENT_SECRET`
env で上書き可・平文コミットなし）を作成する。`bootstrap.sh` がこれを読んで `auth/oidc/config` に渡す。

## bootstrap（runtime 手順・**fail-safe**）

dev Vault はインメモリ（Recreate）＝**Pod 再起動後は再実行**する。要 `vault` CLI ＋ `jq`。

```sh
# 1) Vault へ到達（port-forward）
kubectl -n platform-infra port-forward svc/vault 8200:8200 &

# 2) root トークン（Secret vault-dev-token・既定 devroot）で bootstrap を実行
export VAULT_ADDR=http://localhost:8200
export VAULT_TOKEN=$(kubectl -n platform-infra get secret vault-dev-token -o jsonpath='{.data.token}' | base64 -d)
bash deploy/local/vault/oidc/bootstrap.sh
```

`bootstrap.sh` が行うこと:
- `auth/oidc` を有効化し、`oidc_discovery_url=http://keycloak:8080/realms/microservices-platform`／`client_id=vault`／
  client secret（Secret 由来）で `config`。
- OIDC role `default`（`groups_claim=groups`・`token_policies=default`＝**最小・secret アクセス無し**）。
- policy `admin`（`policies/admin.hcl`）／`operator`（`policies/operator.hcl`）を作成。
- **external group** `platform-admin`→`admin` / `platform-operator`→`operator` を作成し group-alias で OIDC accessor に紐付け。
  realm ロールが `groups` クレームで届き、**external group に一致したユーザーのみ**該当 policy を得る。

**fail-safe**: external group に無い/policy 未マッピングのユーザーは `default` policy のみ＝**secret アクセス不可**（deny 相当）。
root トークンは常に break-glass。

## ログイン / 到達（集約後 URL・#357/edge）

```sh
# edge 集約＋Vault を有効化（ポート再作成が必要・破壊操作はユーザー実行）
k3d cluster delete msp-ast-dev
LOCALEDGE=1 VAULT=1 bash scripts/k8s-local-up.sh
# → 上の bootstrap を一度実行してから:

# UI:  http://vault.localhost:50000 → Method=OIDC → role=default →「Sign in with Keycloak」→ developer/developer
# CLI: vault login -method=oidc role=default    # ブラウザが localhost:8250 の callback を開く
```

- **issuer 整合（#284 手順A）**: browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080`。
  Vault server（platform-infra）は in-cluster の `keycloak:8080` で discovery する。
- **redirect**: UI は `http(s)://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback`（edge admin:50000 は現状 http。
  将来 TLS 化に備え http/https 両方を realm と Vault role に登録済み）。CLI は `http://localhost:8250/oidc/callback`。
- CLI で `*.localhost` 未解決なら hosts 追記 or `*.nip.io`。**realm 反映**: `vault` client は realm 再インポートで有効化。
