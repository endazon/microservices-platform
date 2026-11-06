#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 1・決定 5: OpenBao を開ける（ノード・Pod の再起動のたびに、オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/unseal.sh --live --init-file <init.sh が書いたファイル>.age --identity <age の識別子のファイル>
#
# 鍵は age の復号からパイプで Pod の中の `bao write sys/unseal -` の標準入力へだけ流す（引数・環境変数・ディスクに置かない）。
# 開くまで ESO は値を読めない（同期・証明書の発行が止まる。止まるものは IADR-0689 決定 5）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

INIT_FILE="" IDENTITY=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --init-file) INIT_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--init-file・--identity）" >&2; exit 2 ;;
  esac
done
ob_readable "$INIT_FILE" "init.sh が書いたファイル（--init-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
live_opt_in_require "unseal.sh"
ob_need kubectl age node

# 開けた後、書き込みを受ける状態（active）になるまで待ってから OK を出す（lib.sh の wait_active）。
# 開けた直後の standby の間は読み取りだけが通り、続けて流す configure の失効・seed・put の書き込みが落ちるため。
# shellcheck disable=SC2016
ob_decrypt_lines "$INIT_FILE" "$IDENTITY" unseal_keys_b64 | ob_exec '
set -eu
read -r k
out="$(printf "{\"key\":\"%s\"}" "$k" | bao write -format=json sys/unseal -)"
unset k
case "$out" in
  *"\"sealed\": false"*) ;;
  *) echo "ERROR: 開けなかった（鍵が違うか、OpenBao の状態を確かめる）" >&2; exit 1 ;;
esac
'"$OB_POD_WAIT_ACTIVE"'
rc=0; wait_active || rc=$?
case "$rc" in
  0) ;;
  1) echo "ERROR: 開けたが、'"$OB_ACTIVE_WAIT_S"' 秒待っても書き込みを受ける状態（active）にならない。OpenBao のログ（raft のリーダー選出）を確かめ、unseal.sh を流し直す" >&2; exit 1 ;;
  *) echo "ERROR: 開けた後に、封印中か届かない状態になった。OpenBao の状態を確かめる" >&2; exit 1 ;;
esac
'
echo "OK: OpenBao を開け、書き込みを受ける状態（active）になった（値は表示しない）。ESO の同期が戻るまで待つ: kubectl get externalsecret -A"
