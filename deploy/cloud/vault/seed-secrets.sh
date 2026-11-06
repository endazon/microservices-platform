#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 6・決定 7: クラスタの中で使う資格情報を、**OpenBao の Pod の中で**乱数から作る（オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <configure.sh のファイル>.age --identity <age の識別子>
#       → 7 種（下の表）を作る。在れば触らない（作り直しで稼働中の値と食い違わせない）。
#   bash deploy/cloud/vault/seed-secrets.sh --live --operator-file … --identity … gen <cloud/パス> <キー> <バイト数>
#       → 指定のバイト数の乱数を**標準の base64**（パディングつき・1 行）で書く。キーが在れば止まる（上書きしない）。
#         例（取引ユニットの手順書）: gen cloud/ast/ast-secrets order-approval-signing-key 32
#
# 作る 7 種（ADR-0080 決定 1 の区分 2。パスとキーは IADR-0689 決定 6）:
#   cloud/platform/postgres・keycloak-db・keycloak-admin・rabbitmq  … username（乱数の接尾辞）・password
#   cloud/shared/ast-db                                            … username・password
#   cloud/shared/ast-rabbitmq-users  … deploy/local/rabbitmq-setup/users.txt の取引ユニットの行の利用者名ごとのパスワード
#   cloud/shared/ast-client-secrets  … deploy/cloud/realm-for-cloud.js の CLOUD_CLIENTS（3 つ）ごとの secret
#
# 🔴 値は Pod の外へ出ない（手元へも返さない）。Pod の中で作り、標準入力で bao へ渡す。オーナー用の資格は age の復号から
#    パイプで Pod の標準入力へだけ流す（lib.sh）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
# bash 5.2 の置換は既定で & を一致した字面に読み替える（patsub_replacement）。差し込む名前を字面のまま入れる。
shopt -u patsub_replacement 2>/dev/null || true

OPERATOR_FILE="" IDENTITY="" CMD="seed" ARGS=()
USERS_FILE="${RABBITMQ_USERS_FILE:-$ROOT/deploy/local/rabbitmq-setup/users.txt}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --operator-file) OPERATOR_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    gen) CMD="$1"; shift; ARGS=("$@"); break ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--operator-file・--identity と、gen）" >&2; exit 2 ;;
  esac
done

# 引数の形を検める（値ではない。Pod の中の手順へ字面で入るので、決まった字だけを受ける）。
seed_path_ok() {
  case "$1" in cloud/*) ;; *) return 1 ;; esac
  local rel="${1#cloud/}"
  [[ "$rel" =~ ^(ast|shared|platform|msp)/[a-z0-9][a-z0-9-]*$ ]]
}
seed_key_ok() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; }
case "$CMD" in
  gen)
    [ "${#ARGS[@]}" -eq 3 ] || { echo "ERROR: gen <cloud/パス> <キー> <バイト数>" >&2; exit 2; }
    seed_path_ok "${ARGS[0]}" || { echo "ERROR: パスは cloud/{ast,shared,platform,msp}/<英小文字・数字・->: ${ARGS[0]}" >&2; exit 2; }
    seed_key_ok "${ARGS[1]}" || { echo "ERROR: キーは英数字と . _ - だけ: ${ARGS[1]}" >&2; exit 2; }
    [[ "${ARGS[2]}" =~ ^[0-9]+$ ]] && [ "${ARGS[2]}" -ge 16 ] && [ "${ARGS[2]}" -le 1024 ] \
      || { echo "ERROR: バイト数は 16〜1024: ${ARGS[2]}" >&2; exit 2; }
    ;;
esac
ob_readable "$OPERATOR_FILE" "configure.sh が書いたオーナー用の資格のファイル（--operator-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
live_opt_in_require "seed-secrets.sh"
ob_need kubectl age node

# Pod の中の共通の関数（値を持たない）
# shellcheck disable=SC2016
POD_FN='
# 有無は OB_POD_KV_STATE（lib.sh）で読む。読めなければ書かずに止める（無いと読むと put が既存の値を置き換える）。
have() {
  case "$(path_state "$1")" in present) return 0 ;; absent) return 1 ;;
    *) echo "ERROR: cloud/$1 の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;; esac
}
hex() { head -c "$1" /dev/urandom | od -An -tx1 | tr -d " \n"; }
'

case "$CMD" in
  seed)
    MQ_USERS="$(grep -v '^[[:space:]]*#' "$USERS_FILE" | awk 'NF >= 2 && $2 == "ai-stock-trading" { print $1 }' | tr '\n' ' ')"
    CLIENTS="$(node -e 'process.stdout.write(require(process.argv[1]).CLOUD_CLIENTS.join(" "))' "$ROOT/deploy/cloud/realm-for-cloud.js")"
    for n in $MQ_USERS $CLIENTS; do
      [[ "$n" =~ ^[a-z0-9][a-z0-9-]*$ ]] || { echo "ERROR: 名前の形が違う: $n" >&2; exit 1; }
    done
    [ -n "$MQ_USERS" ] || { echo "ERROR: ${USERS_FILE} に取引ユニットの行が無い" >&2; exit 1; }
    # shellcheck disable=SC2016
    POD_SEED='
userpass() {
  if have "$1"; then echo "    在る: cloud/$1（触らない）"; return 0; fi
  printf "{\"username\":\"%s_%s\",\"password\":\"%s\"}" "$2" "$(hex 3)" "$(hex 24)" | b kv put -mount=cloud "$1" - >/dev/null
  echo "    作った: cloud/$1（username・password）"
}
perkey() {
  p="$1"; shift
  if have "$p"; then echo "    在る: cloud/$p（触らない）"; return 0; fi
  j="{"; sep=""
  for k in "$@"; do j="$j$sep\"$k\":\"$(hex 24)\""; sep=","; done
  printf "%s}" "$j" | b kv put -mount=cloud "$p" - >/dev/null
  unset j
  echo "    作った: cloud/$p（キー: $*）"
}
userpass platform/postgres pgadmin
userpass platform/keycloak-db keycloak
userpass platform/keycloak-admin kcadmin
userpass platform/rabbitmq mqadmin
userpass shared/ast-db ast
perkey shared/ast-rabbitmq-users __MQ_USERS__
perkey shared/ast-client-secrets __CLIENTS__
'
    POD_SEED="${POD_SEED/__MQ_USERS__/$MQ_USERS}"
    POD_SEED="${POD_SEED/__CLIENTS__/$CLIENTS}"
    echo "==> クラスタの中で使う資格情報を OpenBao の中で作る（在れば触らない。値は表示しない）"
    ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id | ob_exec "${OB_POD_LOGIN}${OB_POD_KV_STATE}${POD_FN}${POD_SEED}"
    ;;
  gen)
    REL="${ARGS[0]#cloud/}" KEY="${ARGS[1]}" BYTES="${ARGS[2]}"
    # shellcheck disable=SC2016
    POD_GEN='
p="__REL__"; k="__KEY__"; n="__BYTES__"
if have "$p"; then
  case "$(key_state "$p" "$k")" in
    present) echo "ERROR: cloud/$p の $k は在る。上書きしない" >&2; exit 5 ;;
    absent) op=patch ;;
    *) echo "ERROR: cloud/$p の $k の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;;
  esac
else
  op=put
fi
head -c "$n" /dev/urandom | base64 | tr -d "\n" | b kv "$op" -mount=cloud "$p" "$k=-" >/dev/null
echo "    書いた: cloud/$p の $k（$n バイトの乱数・標準の base64。値は表示しない）"
'
    POD_GEN="${POD_GEN/__REL__/$REL}"; POD_GEN="${POD_GEN/__KEY__/$KEY}"; POD_GEN="${POD_GEN/__BYTES__/$BYTES}"
    ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id | ob_exec "${OB_POD_LOGIN}${OB_POD_KV_STATE}${POD_FN}${POD_GEN}"
    ;;
esac
echo "OK: ${CMD} を終えた（値は表示しない）"
