#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 2: root が要る操作のときだけ、root を**作り直し、操作の後に失効させる**（オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/root-token.sh --live --init-file <init.sh のファイル>.age --operator-file <configure.sh のファイル>.age \
#     --identity <age の識別子> apply-policies
#       → deploy/cloud/vault/policies/*.hcl を当て直す（policy を改めたとき）
#   bash deploy/cloud/vault/root-token.sh --live --init-file … --operator-file … --identity … apply-pki-roles
#       → PKI（pki_cloud）のサーバ証明書の role を lib.sh の OB_PKI_ROLES どおりに書き直す（role を足したとき。IADR-0693。
#         在る role は同じ値で上書きされるだけ。CA と発行済みの証明書は変えない）
#   bash deploy/cloud/vault/root-token.sh --live --init-file … --operator-file … --identity … \
#     rotate-operator --recipients <age の受取人のファイル> --operator-out <新しいファイル>.age
#       → オーナー用の AppRole の secret_id を作り直し、古いものを全部消す（資格のファイルを失った・漏れた疑いのとき）
#
# OpenBao 2.x の root の作り直し（generate-root）は認証が要る（認証の要らない旧の口は既定で閉じている）。オーナー用の資格で入り、
# unseal の鍵を加えて作る（IADR-0689 決定 2）。作り直しの OTP と符号化した root は Pod の中だけにあり、手元へ返さない。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
shopt -u patsub_replacement 2>/dev/null || true

INIT_FILE="" OPERATOR_FILE="" IDENTITY="" CMD="" RECIPIENTS="" OUT=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --init-file) INIT_FILE="${2:-}"; shift 2 ;;
    --operator-file) OPERATOR_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    --recipients) RECIPIENTS="${2:-}"; shift 2 ;;
    --operator-out) OUT="${2:-}"; shift 2 ;;
    apply-policies | apply-pki-roles | rotate-operator) CMD="$1"; shift ;;
    *) echo "ERROR: 未知の引数: $1（操作は apply-policies・apply-pki-roles・rotate-operator のどれか）" >&2; exit 2 ;;
  esac
done
[ -n "$CMD" ] || { echo "ERROR: 操作（apply-policies・apply-pki-roles・rotate-operator のどれか）を与える" >&2; exit 2; }
ob_readable "$INIT_FILE" "init.sh が書いたファイル（--init-file）"
ob_readable "$OPERATOR_FILE" "オーナー用の資格のファイル（--operator-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
if [ "$CMD" = rotate-operator ]; then
  ob_readable "$RECIPIENTS" "age の受取人のファイル（--recipients）"
  ob_fresh "$OUT"
fi
live_opt_in_require "root-token.sh"
ob_need kubectl age node

# Pod の中: オーナー用の資格（1・2 行目）で入り、unseal の鍵（3 行目）で root を作り、操作の後に失効させる。
# shellcheck disable=SC2016
POD_ROOT='
exec 3>&1 1>&2
read -r key
otp="$(b operator generate-root -generate-otp)"
b operator generate-root -cancel >/dev/null 2>&1 || true
b operator generate-root -init -otp="$otp" >/dev/null
nonce="$(b operator generate-root -status -format=json | sed -n "s/.*\"nonce\": *\"\([^\"]*\)\".*/\1/p")"
enc="$(printf "%s" "$key" | b operator generate-root -nonce="$nonce" -format=json - | sed -n "s/.*\"encoded_token\": *\"\([^\"]*\)\".*/\1/p")"
unset key
R="$(printf "%s" "$enc" | b operator generate-root -decode=- -otp="$otp")"
unset enc otp
[ -n "$R" ] || { echo "ERROR: root を作り直せない" >&2; exit 1; }
r() { BAO_TOKEN="$R" bao "$@"; }
finish() { BAO_TOKEN="$R" bao token revoke -self >/dev/null 2>&1 && echo "    作り直した root を失効させた" || echo "WARN: root の失効を確かめられない"; done_revoke; }
trap finish EXIT
echo "    root を作り直した（操作の後に失効させる）"
'
policy_write() {
  printf "r policy write %s - >/dev/null <<'__HCL__'\n" "$1"
  cat "$HERE/policies/${1}.hcl"
  printf '__HCL__\necho "    当てた: policy %s"\n' "$1"
}
case "$CMD" in
  apply-policies)
    OP="$(for p in platform-infra-eso msp-eso ast-eso cloud-operator; do policy_write "$p"; done)"
    { ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id; ob_decrypt_lines "$INIT_FILE" "$IDENTITY" unseal_keys_b64; } \
      | ob_exec "${OB_POD_LOGIN}${POD_ROOT}${OP}"
    ;;
  apply-pki-roles)
    OP="$(ob_pki_roles r)"
    { ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id; ob_decrypt_lines "$INIT_FILE" "$IDENTITY" unseal_keys_b64; } \
      | ob_exec "${OB_POD_LOGIN}${POD_ROOT}${OP}"
    ;;
  rotate-operator)
    # shellcheck disable=SC2016
    OP='
# 一覧は JSON の配列（無ければ「{}」）。accessor の形のものだけを拾う。
for a in $(r list -format=json auth/approle/role/cloud-operator/secret-id 2>/dev/null | tr -d "[]\", " | grep -E "^[0-9a-f-]{36}$" || true); do
  printf "{\"secret_id_accessor\":\"%s\"}" "$a" | r write auth/approle/role/cloud-operator/secret-id-accessor/destroy - >/dev/null
done
rid="$(r read -field=role_id auth/approle/role/cloud-operator/role-id)"
sid="$(r write -f -field=secret_id auth/approle/role/cloud-operator/secret-id)"
printf "{\"role_id\":\"%s\",\"secret_id\":\"%s\"}\n" "$rid" "$sid" >&3
unset rid sid
echo "    作り直した: オーナー用の secret_id（古いものは消した）"
'
    if ! ( umask 077
           { ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id; ob_decrypt_lines "$INIT_FILE" "$IDENTITY" unseal_keys_b64; } \
             | ob_exec "${OB_POD_LOGIN}${POD_ROOT}${OP}" | age -R "$RECIPIENTS" -o "$OUT" ); then
      rm -f "$OUT"
      echo "ERROR: 作り直しに失敗した（書き出し先は消した）" >&2
      exit 1
    fi
    chmod 600 "$OUT"
    echo "    新しい資格を ${OUT} に書いた。古いファイルは使えない（2 か所の保管を差し替える）"
    ;;
esac
echo "OK: ${CMD} を終えた（値は表示しない）"
