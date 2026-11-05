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

# ---- どこまで変えたかの記録（PR #2400 の再レビュー）---------------------------------------------------------------
# 🔴 失敗の文言を手で書かない。各段は成功したら note_done で積み、まだしていない段は TODO に並べる。fail はそれをそのまま告げる。
#   UNDO は「元へ戻せる変更」（ストア側を変える前の保管先・作ったトークン）。ストア側を変えたら commit_point で空にする
#   （ストア側が新しい値になった後に保管先を戻すと、両者が食い違うため）。
DONE=()
TODO=()
UNDO=()
note_done() { DONE+=("$1"); }
set_todo() { TODO=("$@"); }
commit_point() { UNDO=(); }
join() { local IFS='、'; printf '%s' "$*"; }

# undo_all → UNDO を逆順に戻し、戻せたもの・戻せなかったものを告げる
undo_all() {
  local i entry kind rest ok_list=() ng_list=()
  for ((i = ${#UNDO[@]} - 1; i >= 0; i--)); do
    entry="${UNDO[$i]}"
    kind="${entry%%|*}"
    rest="${entry#*|}"
    case "$kind" in
      secret) # secret|<ns>|<Secret>|<キー>|<元の値のファイル>
        IFS='|' read -r u_ns u_secret u_key u_file <<< "$rest"
        if [ -s "$u_file" ] && write_secret "$u_ns" "$u_secret" "$u_key" "$(cat "$u_file")"; then
          ok_list+=("Secret ${u_ns}/${u_secret} の ${u_key}")
        else
          ng_list+=("Secret ${u_ns}/${u_secret} の ${u_key}")
        fi ;;
      kv) # kv|<KV のパス>|<キー>|<元の値のファイル>
        IFS='|' read -r u_kv u_key u_file <<< "$rest"
        if [ -s "$u_file" ] && kv_patch "$u_kv" "$u_key" "$(cat "$u_file")"; then
          ok_list+=("Vault secret/${u_kv} の ${u_key}")
        else
          ng_list+=("Vault secret/${u_kv} の ${u_key}")
        fi ;;
      token) # token|<失効させる新しいトークンのファイル>|<使うトークンのファイル>
        IFS='|' read -r u_new u_use <<< "$rest"
        if printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token revoke '%s' >/dev/null\n" "$(cat "$u_use")" "$(cat "$u_new")" | vault_sh; then
          ok_list+=("新しい固定のトークンを失効させた")
        else
          ng_list+=("新しい固定のトークンの失効")
        fi ;;
    esac
  done
  UNDO=()
  [ "${#ok_list[@]}" -gt 0 ] && echo "       元へ戻した: $(join "${ok_list[@]}")（値は表示しない）" >&2
  [ "${#ng_list[@]}" -gt 0 ] && echo "       🔴 元へ戻せなかった: $(join "${ng_list[@]}")。手順書 secret-rotation-runbook の B-1 で確かめる" >&2
  return 0
}

fail() {
  echo "ERROR: rotate-infra-credentials.sh: $*" >&2
  if [ "${#DONE[@]}" -gt 0 ]; then
    echo "       ここまでに変えたもの: $(join "${DONE[@]}")" >&2
  else
    echo "       ここまでに変えたもの: 無い（何も変えていない）" >&2
  fi
  [ "${#TODO[@]}" -gt 0 ] && echo "       まだしていないもの: $(join "${TODO[@]}")" >&2
  [ "${#UNDO[@]}" -gt 0 ] && undo_all
  exit 1
}

secret_value() { # <ns> <Secret> <キー> → 値（無ければ空）
  kubectl -n "$1" get secret "$2" -o "jsonpath={.data.$3}" 2>/dev/null | base64 -d 2>/dev/null || true
}

# eso_owned <ns> <Secret> → 0＝ExternalSecret が同じ名前で在る（保管先は Vault の KV）
eso_owned() { kubectl -n "$1" get externalsecret "$2" -o name >/dev/null 2>&1; }

# vault_sh → 秘匿管理の Pod の中の sh へ stdin のスクリプトを流す（トークンは Pod の env を使う）
vault_sh() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -s; }

# write_secret <ns> <Secret> <キー> <値> —— Secret のそのキーだけを書く（値は 0600 の一時ファイル経由。引数に載せない）
write_secret() {
  local f="$TMPD/patch.json"
  ( umask 077; printf '{"data":{"%s":"%s"}}' "$3" "$(printf '%s' "$4" | base64 | tr -d '\n')" > "$f" )
  kubectl -n "$1" patch secret "$2" --type merge --patch-file "$f" >/dev/null || { rm -f "$f"; return 1; }
  rm -f "$f"
}

# kv_patch <KV のパス> <キー> <値> / kv_read <KV のパス> <キー> —— Vault の KV（値は stdin のスクリプトにだけ載る）
kv_patch() {
  printf 'printf %%s %s | VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault kv patch secret/%s %s=- >/dev/null\n' \
    "'$3'" "$1" "$2" | vault_sh
}
kv_read() {
  printf 'VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID" vault kv get -field=%s secret/%s 2>/dev/null\n' "$2" "$1" | vault_sh 2>/dev/null || true
}

# save_prev <値> → 元の値を 0600 のファイルへ置き、パスを返す（戻すときに読む。値は表示しない）
save_prev() {
  local f
  f="$(mktemp "$TMPD/prev.XXXXXX")"
  ( umask 077; printf '%s' "$1" > "$f" )
  printf '%s' "$f"
}

# hold <ns> <Secret> <Vault の KV のパス> <キー> <値> —— 保管先へ書く（ESO なら KV・そうでなければ Secret）。
#   書く前に元の値を控え、UNDO へ積む（ストア側を変える前の失敗で戻す）。
hold() {
  local ns="$1" secret="$2" kv="$3" key="$4" value="$5" prev
  if eso_owned "$ns" "$secret"; then
    prev="$(save_prev "$(kv_read "$kv" "$key")")"
    kv_patch "$kv" "$key" "$value" || fail "保管先（Vault secret/${kv} の ${key}）へ書けない"
    UNDO+=("kv|${kv}|${key}|${prev}")
    note_done "Vault secret/${kv} の ${key} を書いた"
  else
    prev="$(save_prev "$(secret_value "$ns" "$secret" "$key")")"
    write_secret "$ns" "$secret" "$key" "$value" || fail "保管先（Secret ${ns}/${secret} の ${key}）へ書けない"
    UNDO+=("secret|${ns}|${secret}|${key}|${prev}")
    note_done "Secret ${ns}/${secret} の ${key} を書いた"
  fi
  echo "    保管先: ${DONE[${#DONE[@]}-1]}（値は表示しない）"
}

# sync_secret <ns> <Secret> <キー> <値> —— ESO なら同期を促し、Secret が新しい値になるまで待つ（値は比べるだけ）
sync_secret() {
  local ns="$1" secret="$2" key="$3" value="$4" deadline
  eso_owned "$ns" "$secret" || return 0
  kubectl -n "$ns" annotate externalsecret "$secret" force-sync="$(date +%s)" --overwrite >/dev/null \
    || fail "ExternalSecret ${ns}/${secret} の同期を促せない"
  # 期限は経過の秒（SECONDS）で見る。待ちの間隔を足し上げる形だと、間隔 0 で終わらない。
  deadline=$((SECONDS + ROTATE_WAIT_SECONDS))
  while [ "$(secret_value "$ns" "$secret" "$key")" != "$value" ]; do
    [ "$SECONDS" -lt "$deadline" ] || fail "Secret ${ns}/${secret} が ${ROTATE_WAIT_SECONDS} 秒のうちに新しい値へ同期されない（同期を促して消費側を作り直す）"
    sleep "$ROTATE_SYNC_INTERVAL"
  done
  note_done "Secret ${ns}/${secret} が新しい値へ同期された"
  echo "    同期: Secret ${ns}/${secret} が新しい値になった"
}

# pg_alter <利用者> <値> —— ストア側（DB の利用者のパスワード）。SQL は stdin でだけ渡す。成功したら元へ戻せない点（commit_point）。
pg_alter() {
  printf 'ALTER ROLE "%s" WITH PASSWORD '"'"'%s'"'"';\n' "$1" "$2" \
    | kubectl -n "$INFRA_NS" exec -i deploy/postgres -c postgres -- psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q -f - >/dev/null \
    || fail "ストア側（DB 利用者 $1）を変えられない"
  commit_point
  note_done "DB 利用者 $1 のパスワードを変えた"
  echo "    ストア側: DB 利用者 $1 のパスワードを変えた"
}

restart_msp() {
  kubectl -n "$MSP_NS" rollout restart deploy >/dev/null || fail "${MSP_NS} の Deployment の作り直しを始められない"
  note_done "${MSP_NS} の Deployment の作り直しを始めた"
  kubectl -n "$MSP_NS" wait --for=condition=Available deploy --all --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "${MSP_NS} の Deployment が ${ROTATE_WAIT_SECONDS} 秒のうちに Available にならない（kubectl -n ${MSP_NS} get deploy で確かめる）"
  echo "    消費側: ${MSP_NS} の Deployment を作り直した"
}

# finish_target <対象> → その対象を済みとして積み、対象ごとの記録を閉じる（次の対象の失敗でも「済んだ対象」として告げる）
finish_target() {
  local keep=("$1 を作り直した")
  DONE=("${COMPLETED[@]+"${COMPLETED[@]}"}" "${keep[@]}")
  COMPLETED=("${DONE[@]}")
  TODO=()
  UNDO=()
}
COMPLETED=()
begin_target() { DONE=("${COMPLETED[@]+"${COMPLETED[@]}"}"); TODO=(); UNDO=(); }

rotate_postgres_admin() {
  echo "==> postgres-admin（DB の管理者）"
  local v
  begin_target
  v="$(new_value)"
  set_todo "ストア側（DB 利用者 postgres）" "同期"
  hold "$INFRA_NS" postgres msp/postgres password "$v"
  pg_alter postgres "$v"
  set_todo "同期"
  sync_secret "$INFRA_NS" postgres password "$v"
  echo "    消費側: 無い（DB の初期化とバックアップの Job が、次に読むときに新しい値を使う）"
  finish_target postgres-admin
}

rotate_app_db() {
  echo "==> app-db（DB 利用者 kp。postgres-app と wikijs-db に同じ値）"
  local v
  begin_target
  v="$(new_value)"
  set_todo "2 つ目の保管先（wikijs-db）" "ストア側（DB 利用者 kp）" "同期" "消費側の作り直し"
  hold "$MSP_NS" postgres-app msp/postgres-app password "$v"
  set_todo "ストア側（DB 利用者 kp）" "同期" "消費側の作り直し"
  hold "$MSP_NS" wikijs-db msp/wikijs-db password "$v"
  pg_alter kp "$v"
  set_todo "同期" "消費側の作り直し"
  sync_secret "$MSP_NS" postgres-app password "$v"
  sync_secret "$MSP_NS" wikijs-db password "$v"
  set_todo "消費側の作り直し"
  restart_msp
  finish_target app-db
}

rotate_object_storage() {
  echo "==> object-storage（オブジェクトストレージ）"
  local v
  begin_target
  v="$(new_value)"
  set_todo "同期" "本体の作り直し" "消費側の作り直し"
  hold "$MSP_NS" object-storage-credentials msp/object-storage-credentials secretKey "$v"
  # ストア側は無い（起動時の環境変数で決まる）。ここから先は本体を作り直すので元へ戻さない。
  commit_point
  sync_secret "$MSP_NS" object-storage-credentials secretKey "$v"
  set_todo "本体の作り直し" "消費側の作り直し"
  kubectl -n "$MSP_NS" rollout restart deploy/seaweedfs >/dev/null || fail "seaweedfs の作り直しを始められない"
  note_done "seaweedfs の作り直しを始めた"
  kubectl -n "$MSP_NS" rollout status deploy/seaweedfs --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "seaweedfs が ${ROTATE_WAIT_SECONDS} 秒のうちに立ち上がらない"
  set_todo "消費側の作り直し"
  restart_msp
  finish_target object-storage
}

rotate_vault_root_token() {
  echo "==> vault-root-token（秘匿管理の固定の管理者トークン）"
  local old new oldf newf
  begin_target
  old="$(secret_value "$INFRA_NS" vault-dev-token token)"
  [ -n "$old" ] || fail "Secret ${INFRA_NS}/vault-dev-token が読めない"
  new="$(new_value)"
  oldf="$(save_prev "$old")"
  newf="$(save_prev "$new")"
  set_todo "新しい固定のトークンの作成" "Secret の書き込み" "秘匿管理の作り直し" "新しいトークンの確かめ" "古いトークンの失効"
  # ストア側を先に: 今のトークンで新しい固定のトークンを作る（永続化のラッパーと同じ作り方）。
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token create -id='%s' -policy=root -orphan -no-default-policy -display-name=local-dev-root >/dev/null\n" \
    "$old" "$new" | vault_sh || fail "新しい固定のトークンを作れない"
  UNDO+=("token|${newf}|${oldf}")
  note_done "新しい固定のトークンを作った（古いトークンは未失効）"
  echo "    ストア側: 新しい固定のトークンを作った"
  set_todo "Secret の書き込み" "秘匿管理の作り直し" "新しいトークンの確かめ" "古いトークンの失効"
  # 保管先は Secret だけ（IADR-0672 決定 5）。vault-dev-token は秘匿管理そのものの鍵なので ESO が持たない（ESO の store がこの Secret を読む）。
  #   eso_owned は見ない —— 同じ名前の ExternalSecret が在っても Vault の KV へは書かない（書き先の KV が無い）。
  write_secret "$INFRA_NS" vault-dev-token token "$new" || fail "Secret ${INFRA_NS}/vault-dev-token の token を書けない（Secret は古いトークンのまま）"
  # Secret が新しいトークンになった後は、作ったトークンを失効させない（秘匿管理が次に読むのは新しいトークン）。
  commit_point
  note_done "Secret ${INFRA_NS}/vault-dev-token の token を書いた"
  echo "    保管先: Secret ${INFRA_NS}/vault-dev-token の token を書いた（値は表示しない）"
  set_todo "秘匿管理の作り直し" "新しいトークンの確かめ" "古いトークンの失効"
  kubectl -n "$INFRA_NS" rollout restart deploy/vault >/dev/null || fail "秘匿管理の作り直しを始められない"
  note_done "秘匿管理の作り直しを始めた"
  kubectl -n "$INFRA_NS" rollout status deploy/vault --timeout="${ROTATE_WAIT_SECONDS}s" >/dev/null \
    || fail "秘匿管理が ${ROTATE_WAIT_SECONDS} 秒のうちに立ち上がらない"
  set_todo "新しいトークンの確かめ" "古いトークンの失効"
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token lookup >/dev/null\n" "$new" | vault_sh \
    || fail "新しいトークンで通らない（Secret を戻すなら手順書の B-1 を見る）"
  note_done "新しいトークンで通ることを確かめた"
  set_todo "古いトークンの失効"
  printf "VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token revoke '%s' >/dev/null\n" "$new" "$old" | vault_sh \
    || fail "古いトークンを失効させられない（新しいトークンは使える。古いトークンを手で失効させる）"
  echo "    消費側: 秘匿管理を作り直し、新しいトークンで通ることを確かめ、古いトークンを失効させた（ESO の store は Secret を読み直す）"
  finish_target vault-root-token
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
