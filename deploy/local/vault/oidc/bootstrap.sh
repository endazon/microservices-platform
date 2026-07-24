#!/usr/bin/env bash
# IADR-0094 (#353): dev Vault(-dev) を Keycloak OIDC(auth/oidc) へ連携する runtime bootstrap（再実行可）。
#
#   VAULT_ADDR=http://localhost:8200 VAULT_TOKEN=<root> bash deploy/local/vault/oidc/bootstrap.sh
#
# 前提: vault CLI / jq。dev Vault はインメモリ（Recreate）＝Pod 再起動後は本 bootstrap を再実行する。
# 事前に port-forward: kubectl -n platform-infra port-forward svc/vault 8200:8200
# root トークンは Secret vault-dev-token（k8s-local-up.sh が作成・既定 devroot）。
#
# fail-safe: OIDC role の既定 policy は `default`（最小・secret アクセス無し）。platform-admin/platform-operator は
# Vault external group（realm ロールの groups クレーム）経由で admin/operator policy を得る。未マッピングは default のみ。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
VAULT_ADDR="${VAULT_ADDR:-http://localhost:8200}"; export VAULT_ADDR
: "${VAULT_TOKEN:?VAULT_TOKEN（root）を設定してください（Secret vault-dev-token の値・既定 devroot）}"; export VAULT_TOKEN

# OIDC client secret は平文で置かず Secret vault-oidc から読む（env で上書き可）。
CLIENT_SECRET="${VAULT_OIDC_CLIENT_SECRET:-}"
if [ -z "$CLIENT_SECRET" ]; then
  CLIENT_SECRET="$(kubectl -n platform-infra get secret vault-oidc -o jsonpath='{.data.client-secret}' 2>/dev/null | base64 -d || true)"
fi
: "${CLIENT_SECRET:?vault-oidc secret も VAULT_OIDC_CLIENT_SECRET env も無い。k8s-local-up.sh を VAULT=1 で実行済みか確認}"

ISSUER="${VAULT_OIDC_DISCOVERY_URL:-http://keycloak:8080/realms/microservices-platform}"
REDIRECTS="http://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback,https://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback,http://localhost:8250/oidc/callback"

echo "==> auth/oidc を有効化（既に有効なら無視）"
vault auth enable oidc 2>/dev/null || echo "    oidc は既に有効"
ACCESSOR="$(vault auth list -format=json | jq -r '."oidc/".accessor')"

echo "==> auth/oidc/config"
vault write auth/oidc/config \
  oidc_discovery_url="$ISSUER" \
  oidc_client_id="vault" \
  oidc_client_secret="$CLIENT_SECRET" \
  default_role="default"

echo "==> auth/oidc/role/default（既定 policy=default＝fail-safe・secret アクセスは group 経由でのみ）"
vault write auth/oidc/role/default \
  bound_audiences="vault" \
  allowed_redirect_uris="$REDIRECTS" \
  user_claim="preferred_username" \
  groups_claim="groups" \
  oidc_scopes="openid,profile,email" \
  token_policies="default"

echo "==> policy: admin / operator"
vault policy write admin    "$ROOT/deploy/local/vault/oidc/policies/admin.hcl"
vault policy write operator "$ROOT/deploy/local/vault/oidc/policies/operator.hcl"

# external group（realm ロール名 == groups クレーム値）→ policy を紐付ける。group-alias が OIDC accessor と対応。
create_group() { # name policy
  local name="$1" policy="$2"
  vault write "identity/group" name="$name" type="external" policies="$policy" >/dev/null 2>&1 || \
    vault write "identity/group/name/$name" policies="$policy" >/dev/null
  local gid; gid="$(vault read -field=id "identity/group/name/$name")"
  vault write identity/group-alias name="$name" mount_accessor="$ACCESSOR" canonical_id="$gid" >/dev/null
  echo "    external group '$name' → policy '$policy'"
}
echo "==> external groups（realm ロール→policy）"
create_group platform-admin    admin
create_group platform-operator operator

echo ""
echo "done. Vault OIDC ログイン:"
echo "  UI : http://vault.localhost:50000 （LOCALEDGE=1・Method=OIDC・role=default）"
echo "  CLI: vault login -method=oidc role=default"
echo "  例: developer/developer（platform-admin/operator 保持）→ admin+operator policy。未マッピングは default のみ。"
