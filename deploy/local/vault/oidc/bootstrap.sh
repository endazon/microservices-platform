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

ISSUER="${VAULT_OIDC_DISCOVERY_URL:-http://keycloak:8080/realms/platform}"
# IADR-0220 (#841): admin(50000) が TLS 終端になったため UI の redirect は https のみ（NFR-11「平文 HTTP を残さない」）。
# CLI の localhost:8250 はエッジを経由しないローカル callback であり対象外。
REDIRECTS="https://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback,http://localhost:8250/oidc/callback"

echo "==> auth/oidc を有効化（未有効時のみ・冪等）"
# 既有効かを先に判定し、未有効のときだけ enable する（enable 失敗＝権限不足等は握りつぶさず set -e で止める）。
if vault auth list -format=json | jq -e '."oidc/"' >/dev/null 2>&1; then
  echo "    oidc は既に有効"
else
  vault auth enable oidc
fi
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

# IADR-0103 (#354): auth mount の listing_visibility は既定 hidden で、未認証の
# `sys/internal/ui/mounts` が `auth: {}` を返す。その状態では **Vault UI のログイン画面に OIDC が現れず**
# Token 入力しか見えないため「ログインできない」ように見える。unauth にして選択肢として提示する。
echo "==> auth/oidc を UI のログイン候補に表示（listing_visibility=unauth）"
vault auth tune -listing-visibility=unauth -description="Keycloak SSO (OIDC)" oidc/

echo ""
echo "done. Vault OIDC ログイン:"
echo "  UI : https://vault.localhost:50000 （LOCALEDGE=1・Method=OIDC・role=default）"
echo "  CLI: vault login -method=oidc role=default"
echo "  例: developer/developer（platform-admin/operator 保持）→ admin+operator policy。未マッピングは default のみ。"
