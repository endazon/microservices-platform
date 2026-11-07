#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 6・決定 7: クラスタの中で使う資格情報を、**OpenBao の Pod の中で**乱数から作る（オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <configure.sh のファイル>.age --identity <age の識別子>
#       → 8 種（下の表。取引ユニットの DB はサービスごとの 7 組も）を作る。在る値は触らない（作り直しで稼働中の値と食い違わせない）。
#         username・password の組（userpass）はパスが在れば触らない。キーごとの表（perkey）は在るパスに無いキーだけを足す（IADR-0700）。
#   bash deploy/cloud/vault/seed-secrets.sh --live --operator-file … --identity … gen <cloud/パス> <キー> <バイト数>
#       → 指定のバイト数の乱数を**標準の base64**（パディングつき・1 行）で書く。キーが在れば止まる（上書きしない）。
#         例（取引ユニットの手順書）: gen cloud/ast/ast-secrets order-approval-signing-key 32
#   bash deploy/cloud/vault/seed-secrets.sh --live --operator-file … --identity … rotate <cloud/パス> <キー>
#       → NFR-18, IADR-0706: seed が作った値（下の表）の**在るキーだけ**を、Pod の中の乱数（24 バイト・16 進）で作り直す。ほかのキーは触らない。
#         キーが無ければ止まる（作るのは seed）。userpass のパスは password だけ（利用者名は変えない）。消費側への反映は手順書
#         docs/operations/cloud-credential-rotation-runbook.md（値を作り直しただけでは、Keycloak・Postgres の中の値は変わらない）。
#
# 作る 8 種（ADR-0080 決定 1 の区分 2。パスとキーは IADR-0689 決定 6）:
#   cloud/platform/postgres・keycloak-db・keycloak-admin・rabbitmq  … username（乱数の接尾辞）・password
#   cloud/platform/postgres-backup                                … username（pgbackup_<乱数>）・password（IADR-0706。バックアップの利用者）
#   cloud/shared/ast-db                                            … username・password（共有の利用者。移行の間だけ。IADR-0695）
#   cloud/shared/ast-db-<サービス>（7 つ）                         … username（ast_<サービス>_<乱数>）・password（IADR-0695。サービスごとの DB の利用者）
#   cloud/shared/ast-rabbitmq-users  … deploy/local/rabbitmq-setup/users.txt の取引ユニットの行の利用者名ごとのパスワード（perkey）
#   cloud/shared/ast-client-secrets  … deploy/cloud/realm-for-cloud.js の CLOUD_CLIENTS（宣言の 3 つと、サービスごとの 7 つ。IADR-0700）ごとの secret（perkey）
#     perkey の 2 つは、在るパスには無いキーだけを足す（在るキーの値は触らない。users.txt に行が増えれば稼働中のパスにもキーが増える）。
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
    gen | rotate) CMD="$1"; shift; ARGS=("$@"); break ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--operator-file・--identity と、gen・rotate）" >&2; exit 2 ;;
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
  rotate)
    [ "${#ARGS[@]}" -eq 2 ] || { echo "ERROR: rotate <cloud/パス> <キー>" >&2; exit 2; }
    # seed が作る値だけ（gen の値・外から持ち込む値は形が違うので、ここでは作り直さない）。
    case "${ARGS[0]}" in
      cloud/platform/postgres | cloud/platform/postgres-backup | cloud/platform/keycloak-db | cloud/platform/keycloak-admin | cloud/platform/rabbitmq | cloud/shared/ast-db | cloud/shared/ast-db-*)
        [ "${ARGS[1]}" = password ] || { echo "ERROR: ${ARGS[0]} で作り直せるのは password だけ（利用者名は変えない）" >&2; exit 2; } ;;
      cloud/shared/ast-rabbitmq-users | cloud/shared/ast-client-secrets) ;;
      *) echo "ERROR: rotate は seed が作る値だけ（cloud/platform/{postgres,postgres-backup,keycloak-db,keycloak-admin,rabbitmq}・cloud/shared/ast-db*・ast-rabbitmq-users・ast-client-secrets）: ${ARGS[0]}" >&2; exit 2 ;;
    esac
    seed_path_ok "${ARGS[0]}" || { echo "ERROR: パスの形が違う: ${ARGS[0]}" >&2; exit 2; }
    seed_key_ok "${ARGS[1]}" || { echo "ERROR: キーは英数字と . _ - だけ: ${ARGS[1]}" >&2; exit 2; }
    ;;
esac
ob_readable "$OPERATOR_FILE" "configure.sh が書いたオーナー用の資格のファイル（--operator-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
ob_identity_guard "$IDENTITY"
live_opt_in_require "seed-secrets.sh"
ob_need kubectl age node
ct_require_target "seed-secrets.sh"

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
  if have "$p"; then
    # IADR-0700: 在るパスには、無いキーだけを足す（在るキーの値は触らない。読めないキーがあれば書かずに止める）。
    miss=""
    for k in "$@"; do
      case "$(key_state "$p" "$k")" in present) ;; absent) miss="$miss $k" ;;
        *) echo "ERROR: cloud/$p の $k の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;; esac
    done
    if [ -z "$miss" ]; then echo "    在る: cloud/$p（触らない）"; return 0; fi
    j="{"; sep=""
    for k in $miss; do j="$j$sep\"$k\":\"$(hex 24)\""; sep=","; done
    printf "%s}" "$j" | b kv patch -mount=cloud "$p" - >/dev/null
    unset j
    echo "    足した: cloud/$p（在るキーは触らない。足したキー:$miss）"
    return 0
  fi
  j="{"; sep=""
  for k in "$@"; do j="$j$sep\"$k\":\"$(hex 24)\""; sep=","; done
  printf "%s}" "$j" | b kv put -mount=cloud "$p" - >/dev/null
  unset j
  echo "    作った: cloud/$p（キー: $*）"
}
userpass platform/postgres pgadmin
# IADR-0706: バックアップの利用者（超利用者でない。利用者は Job の段 platform が作る。手順書）
userpass platform/postgres-backup pgbackup
userpass platform/keycloak-db keycloak
userpass platform/keycloak-admin kcadmin
userpass platform/rabbitmq mqadmin
userpass shared/ast-db ast
# IADR-0695: 取引ユニットの DB の利用者をサービスごとに（各利用者が自分の DB だけを持つ。利用者は Job が作る。手順書）
userpass shared/ast-db-audit ast_audit
userpass shared/ast-db-configuration ast_configuration
userpass shared/ast-db-cost-control ast_cost_control
userpass shared/ast-db-market-monitor ast_market_monitor
userpass shared/ast-db-order-execution ast_order_execution
userpass shared/ast-db-report ast_report
userpass shared/ast-db-risk-management ast_risk_management
perkey shared/ast-rabbitmq-users __MQ_USERS__
perkey shared/ast-client-secrets __CLIENTS__
'
    POD_SEED="${POD_SEED/__MQ_USERS__/$MQ_USERS}"
    POD_SEED="${POD_SEED/__CLIENTS__/$CLIENTS}"
    echo "==> クラスタの中で使う資格情報を OpenBao の中で作る（在る値は触らない。無いキーだけを足す。値は表示しない）"
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
  rotate)
    REL="${ARGS[0]#cloud/}" KEY="${ARGS[1]}"
    # shellcheck disable=SC2016
    POD_ROTATE='
p="__REL__"; k="__KEY__"
case "$(key_state "$p" "$k")" in
  present) ;;
  absent) echo "ERROR: cloud/$p の $k が無い（作るのは seed）。作り直さない" >&2; exit 5 ;;
  *) echo "ERROR: cloud/$p の $k の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;;
esac
printf "{\"%s\":\"%s\"}" "$k" "$(hex 24)" | b kv patch -mount=cloud "$p" - >/dev/null
echo "    作り直した: cloud/$p の $k（24 バイトの乱数。ほかのキーは触らない。値は表示しない）"
'
    POD_ROTATE="${POD_ROTATE/__REL__/$REL}"; POD_ROTATE="${POD_ROTATE/__KEY__/$KEY}"
    echo "==> cloud/${REL} の ${KEY} を OpenBao の中で作り直す（値は表示しない。消費側への反映は手順書）"
    ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id | ob_exec "${OB_POD_LOGIN}${OB_POD_KV_STATE}${POD_FN}${POD_ROTATE}"
    ;;
esac
echo "OK: ${CMD} を終えた（値は表示しない）"
