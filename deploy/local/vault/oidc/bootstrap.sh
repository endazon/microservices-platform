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

# IADR-0243 決定 3 / #780: issuer を **https のエッジ host** へ移した。
# 🔴 **Vault は discovery URL とその文書の `issuer` が一致していないと config 書き込み自体を拒む**
#    （"error checking oidc discovery URL"）。したがって in-cluster 名を残す選択肢が無い ——
#    Keycloak が広告する issuer はエッジ host 1 本だからである（KC_HOSTNAME_URL・IADR-0243 決定 1）。
ISSUER="${VAULT_OIDC_DISCOVERY_URL:-https://keycloak.localhost/realms/platform}"
# エッジ証明書はローカル CA（cert-manager の local-edge-ca）が署名しており、Vault コンテナの
# 既定ルートには入っていない。Vault は `oidc_discovery_ca_pem` を一次サポートするので、
# CA をクラスタから読んでその場で渡す（**リポジトリへ PEM を焼き込まない**）。
# fail-safe: CA が取れなければ空のまま渡す（＝既定ルートで検証する）。http の issuer を
# 指定した場合も空のままでよい（Vault は ca_pem を無視する）。
CA_PEM="${VAULT_OIDC_DISCOVERY_CA_PEM:-}"
if [ -z "$CA_PEM" ] && printf %s "$ISSUER" | grep -q "^https://"; then
  CA_PEM="$(kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' 2>/dev/null | base64 -d 2>/dev/null || true)"
  [ -z "$CA_PEM" ] && echo "    warn: cert-manager/local-edge-root-ca が読めない。既定ルートで検証する（自己署名なら失敗する）"
fi
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
  oidc_discovery_ca_pem="$CA_PEM" \
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
