#!/usr/bin/env bash
# NFR-18, ADR-0124 決定 1, IADR-0672 (#2399): **基盤の資格情報を乱数の値で作り直す**（利用者が実行する）。
#
#   bash scripts/rotate-infra-credentials.sh --live <対象>...
#     postgres-admin    DB の管理者（Secret platform-infra/postgres・Vault msp/postgres）
#     app-db            DB 利用者 kp（Secret microservices-platform/postgres-app と wikijs-db・Vault msp/postgres-app と msp/wikijs-db。同じ値）
#     object-storage    オブジェクトストレージ（Secret microservices-platform/object-storage-credentials の secretKey・Vault 同名）
#     vault-root-token  秘匿管理の固定の管理者トークン（Secret platform-infra/vault-dev-token）
#
# 順序は docs/operations/secret-rotation-runbook.md B-1 と同じ: 保管先 → ストア側 → 同期 → 消費側の作り直し。
#   保管先は、Secret を ExternalSecret が持つ（ESO）なら Vault の KV、そうでなければ Secret そのもの。
# 🔴 新しい値は乱数（16 進 48 字）で作り、**表示しない**。値は 0600 の一時ファイル（終了時に消す）か stdin でだけ渡し、
#    どのプロセスの引数にも載せない。🔴 DB 利用者 kp の切り替えの間、DB を使うサービスは断続的に止まる（業務の少ない時間に行う）。
# 再実行で値が戻らないのは、起動器が既存の Secret の値を引き継ぐため（IADR-0672 決定 2）。
# 終了コード: 0＝成功 / 1＝失敗（どこで止まったかを告げる） / 2＝引数の誤り / 3＝--live が無い。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "rotate-infra-credentials.sh"

INFRA_NS="${INFRA_NS:-platform-infra}"
MSP_NS="${MSP_NS:-microservices-platform}"
ROTATE_WAIT_SECONDS="${ROTATE_WAIT_SECONDS:-300}"
ROTATE_SYNC_INTERVAL="${ROTATE_SYNC_INTERVAL:-3}"

usage() {
  echo "使い方: bash scripts/rotate-infra-credentials.sh --live <postgres-admin|app-db|object-storage|vault-root-token>..." >&2
  exit 2
}
[ "$#" -gt 0 ] || usage
for t in "$@"; do
  case "$t" in
    postgres-admin | app-db | object-storage | vault-root-token) ;;
    *) echo "ERROR: 未知の対象: ${t}" >&2; usage ;;
  esac
done

TMPD="$(mktemp -d)"
chmod 700 "$TMPD"
trap 'rm -rf "$TMPD"' EXIT

new_value() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }
fail() { echo "ERROR: rotate-infra-credentials.sh: $*" >&2; exit 1; }

secret_value() { # <ns> <Secret> <キー> → 値（無ければ空）
  kubectl -n "$1" get secret "$2" -o "jsonpath={.data.$3}" 2>/dev/null | base64 -d 2>/dev/null || true
}

# eso_owned <ns> <Secret> → 0＝ExternalSecret が同じ名前で在る（保管先は Vault の KV）
eso_owned() { kubectl -n "$1" get externalsecret "$2" -o name >/dev/null 2>&1; }

# vault_sh → 秘匿管理の Pod の中の sh へ stdin のスクリプトを流す（トークンは Pod の env を使う）
vault_sh() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -s; }

# write_secret <ns> <Secret> <キー> <値> —— Secret のそのキーだけを書く（値は 0600 の一時ファイル経由。引数に載せない）。
#   失敗は終了コードで返す（何を変えたかは呼び出し側が告げる）。
write_secret() {
  local f="$TMPD/patch.json"
  ( umask 077; printf '{"data":{"%s":"%s"}}' "$3" "$(printf '%s' "$4" | base64 | tr -d '\n')" > "$f" )
  kubectl -n "$1" patch secret "$2" --type merge --patch-file "$f" >/dev/null || { rm -f "$f"; return 1; }
  rm -f "$f"
}

# hold <ns> <Secret> <Vault の KV のパス> <キー> <値> —— 保管先へ書く（ESO なら KV・そうでなければ Secret）
#   ストア側より前に呼ぶ（失敗の文言「ストア側は変えていない」はこの順序に依る）。
hold() {
  local ns="$1" secret="$2" kv="$3" key="$4" value="$5"
  if eso_owned "$ns" "$secret"; then
    printf 'printf %%s %s | VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault kv patch secret/%s %s=- >/dev/null\n' \
      "'$value'" "$kv" "$key" | vault_sh || fail "保管先（Vault の ${kv}）へ書けない。ストア側は変えていない"
    echo "    保管先: Vault secret/${kv} の ${key} を書いた（値は表示しない）"
  else
    write_secret "$ns" "$secret" "$key" "$value" || fail "保管先（Secret ${ns}/${secret}）へ書けない。ストア側は変えていない"
    echo "    保管先: Secret ${ns}/${secret} の ${key} を書いた（値は表示しない）"
  fi
}

# sync_secret <ns> <Secret> <キー> <値> —— ESO なら同期を促し、Secret が新しい値になるまで待つ（値は比べるだけ）
sync_secret() {
  local ns="$1" secret="$2" key="$3" value="$4" waited=0
  eso_owned "$ns" "$secret" || return 0
  kubectl -n "$ns" annotate externalsecret "$secret" force-sync="$(date +%s)" --overwrite >/dev/null
  while [ "$(secret_value "$ns" "$secret" "$key")" != "$value" ]; do
    [ "$waited" -lt "$ROTATE_WAIT_SECONDS" ] || fail "Secret ${ns}/${secret} が ${ROTATE_WAIT_SECONDS} 秒のうちに新しい値へ同期されない（保管先とストア側は新しい値。同期を促して消費側を作り直す）"
    sleep "$ROTATE_SYNC_INTERVAL"
    waited=$((waited + ROTATE_SYNC_INTERVAL))
  done
  echo "    同期: Secret ${ns}/${secret} が新しい値になった"
}

# pg_alter <利用者> <値> —— ストア側（DB の利用者のパスワード）。SQL は stdin でだけ渡す。
pg_alter() {
  printf 'ALTER ROLE "%s" WITH PASSWORD '"'"'%s'"'"';\n' "$1" "$2" \
    | kubectl -n "$INFRA_NS" exec -i deploy/postgres -c postgres -- psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q -f - >/dev/null \
    || fail "ストア側（DB 利用者 $1）を変えられない。保管先は新しい値になっている。DB を確かめてから再実行する（再実行は新しい値で作り直す）"
  echo "    ストア側: DB 利用者 $1 のパスワードを変えた"
}

restart_msp() {
  kubectl -n "$MSP_NS" rollout restart deploy >/dev/null
  kubectl -n "$MSP_NS" wait --for=condition=Available deploy --all --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "${MSP_NS} の Deployment が ${ROTATE_WAIT_SECONDS} 秒のうちに Available にならない（値は書いた。kubectl -n ${MSP_NS} get deploy で確かめる）"
  echo "    消費側: ${MSP_NS} の Deployment を作り直した"
}

rotate_postgres_admin() {
  echo "==> postgres-admin（DB の管理者）"
  local v
  v="$(new_value)"
  hold "$INFRA_NS" postgres msp/postgres password "$v"
  pg_alter postgres "$v"
  sync_secret "$INFRA_NS" postgres password "$v"
  echo "    消費側: 無い（DB の初期化とバックアップの Job が、次に読むときに新しい値を使う）"
}

rotate_app_db() {
  echo "==> app-db（DB 利用者 kp。postgres-app と wikijs-db に同じ値）"
  local v
  v="$(new_value)"
  hold "$MSP_NS" postgres-app msp/postgres-app password "$v"
  hold "$MSP_NS" wikijs-db msp/wikijs-db password "$v"
  pg_alter kp "$v"
  sync_secret "$MSP_NS" postgres-app password "$v"
  sync_secret "$MSP_NS" wikijs-db password "$v"
  restart_msp
}

rotate_object_storage() {
  echo "==> object-storage（オブジェクトストレージ）"
  local v
  v="$(new_value)"
  hold "$MSP_NS" object-storage-credentials msp/object-storage-credentials secretKey "$v"
  sync_secret "$MSP_NS" object-storage-credentials secretKey "$v"
  echo "    ストア側: 無い（起動時の環境変数で決まる）。本体を先に作り直す"
  kubectl -n "$MSP_NS" rollout restart deploy/seaweedfs >/dev/null
  kubectl -n "$MSP_NS" rollout status deploy/seaweedfs --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "seaweedfs が ${ROTATE_WAIT_SECONDS} 秒のうちに立ち上がらない（値は書いた）"
  restart_msp
}

rotate_vault_root_token() {
  echo "==> vault-root-token（秘匿管理の固定の管理者トークン）"
  local old new
  old="$(secret_value "$INFRA_NS" vault-dev-token token)"
  [ -n "$old" ] || fail "Secret ${INFRA_NS}/vault-dev-token が読めない（何も変えていない）"
  new="$(new_value)"
  # ストア側を先に: 今のトークンで新しい固定のトークンを作る（永続化のラッパーと同じ作り方）。
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token create -id='%s' -policy=root -orphan -no-default-policy -display-name=local-dev-root >/dev/null\n" \
    "$old" "$new" | vault_sh || fail "新しいトークンを作れない（何も変えていない）"
  echo "    ストア側: 新しい固定のトークンを作った"
  # 保管先は Secret だけ（IADR-0672 決定 5）。vault-dev-token は秘匿管理そのものの鍵なので ESO が持たない（ESO の store がこの Secret を読む）。
  #   eso_owned は見ない —— 同じ名前の ExternalSecret が在っても Vault の KV へは書かない（書き先の KV が無い）。
  # 🔴 ここから先の失敗は、新しいトークンを作った後である。どこまで変えたかを事実どおりに告げる。
  write_secret "$INFRA_NS" vault-dev-token token "$new" \
    || fail "Secret ${INFRA_NS}/vault-dev-token へ書けない。新しいトークンは作成済み（Secret は古いトークンのまま）。古いトークンは失効させていない（秘匿管理は古いトークンで動き続ける。再実行すると別の新しいトークンを作る）"
  echo "    保管先: Secret ${INFRA_NS}/vault-dev-token の token を書いた（値は表示しない）"
  kubectl -n "$INFRA_NS" rollout restart deploy/vault >/dev/null \
    || fail "秘匿管理を作り直せない。新しいトークンは作成済みで、Secret は新しいトークン。古いトークンは失効させていない"
  kubectl -n "$INFRA_NS" rollout status deploy/vault --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "秘匿管理が ${ROTATE_WAIT_SECONDS} 秒のうちに立ち上がらない。新しいトークンは作成済みで、Secret は新しいトークン。古いトークンは失効させていない"
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token lookup >/dev/null\n" "$new" | vault_sh \
    || fail "新しいトークンで通らない。新しいトークンは作成済みで、Secret は新しいトークン。古いトークンは失効させていない（Secret を戻すなら手順書の B-1 を見る）"
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token revoke '%s' >/dev/null\n" "$new" "$old" | vault_sh \
    || fail "古いトークンを失効させられない。新しいトークンは作成済みで使える（Secret も新しいトークン）。古いトークンを手で失効させる"
  echo "    消費側: 秘匿管理を作り直し、新しいトークンで通ることを確かめ、古いトークンを失効させた（ESO の store は Secret を読み直す）"
}

for t in "$@"; do
  case "$t" in
    postgres-admin) rotate_postgres_admin ;;
    app-db) rotate_app_db ;;
    object-storage) rotate_object_storage ;;
    vault-root-token) rotate_vault_root_token ;;
  esac
done
echo "OK: 作り直した: $*（値は表示しない）。bash scripts/check-infra-credentials.sh --live で確かめる"
