#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 11: オーナーが**外から持ち込む値**（ADR-0080 決定 1 の区分 1）を、OpenBao の cloud/<領域>/<名前> へ書く口。
#
#   <値を出すもの> | bash deploy/cloud/vault/put-external-secret.sh --live --operator-file <configure.sh のファイル>.age \
#                      --identity <age の識別子> [--overwrite] cloud/<ast|platform|msp>/<名前> <キー>
#
# - **値は標準入力からだけ受ける**（引数・環境変数・ファイルに置かない）。手元では値をパイプで流す。例（形だけ。値は書かない）:
#     read -rs v; printf '%s' "$v" | bash deploy/cloud/vault/put-external-secret.sh --live … cloud/msp/llm-provider-credentials anthropic-api-key; unset v
#     openssl genrsa -traditional 1024 | bash deploy/cloud/vault/put-external-secret.sh --live … cloud/ast/moomoo-rsa opend_rsa.pem
# - 標準入力の中身をそのまま 1 つのキーの値にする（末尾の改行も含めて変えない）。空なら書かない。
# - **キーが在れば止まる**（終了コード 5）。上書きは `--overwrite` を与えたときだけ（同じパスのほかのキーは残す）。
# - 書けるのは cloud/ast・cloud/platform・cloud/msp の下だけ。cloud/shared（基盤と取引ユニットが共有する値。seed-secrets.sh が
#   OpenBao の中で作る）へは書かない。
# - Pod の中では値を表示しない。オーナー用の資格は age の復号からパイプで Pod の標準入力へだけ流す（lib.sh）。
#
# 終了コード: 0＝書いた / 1＝失敗（有無を読めないときも書かずに 1） / 2＝引数の誤り・値が空 / 3＝--live が無い / 5＝キーが在る（--overwrite なし。何も変えない）
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

OPERATOR_FILE="" IDENTITY="" OVERWRITE=0 POS=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --operator-file) OPERATOR_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    --overwrite) OVERWRITE=1; shift ;;
    -*) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--operator-file・--identity・--overwrite と、<パス> <キー>）" >&2; exit 2 ;;
    *) POS+=("$1"); shift ;;
  esac
done
[ "${#POS[@]}" -eq 2 ] || { echo "ERROR: cloud/<ast|platform|msp>/<名前> <キー> の 2 つを与える（値は標準入力）" >&2; exit 2; }
P="${POS[0]}" K="${POS[1]}"
[[ "$P" =~ ^cloud/(ast|platform|msp)/[a-z0-9][a-z0-9-]*$ ]] \
  || { echo "ERROR: パスは cloud/{ast,platform,msp}/<英小文字・数字・->（cloud/shared へは書かない）: $P" >&2; exit 2; }
[[ "$K" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo "ERROR: キーは英数字と . _ - だけ: $K" >&2; exit 2; }
[ ! -t 0 ] || { echo "ERROR: 値は標準入力から流す（端末から直に打たない。read -rs で読んでからパイプで渡す）" >&2; exit 2; }
ob_readable "$OPERATOR_FILE" "configure.sh が書いたオーナー用の資格のファイル（--operator-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
live_opt_in_require "put-external-secret.sh"
ob_need kubectl age node

REL="${P#cloud/}"
# Pod の中の手順（値を持たない）。1・2 行目はオーナー用の資格、その後ろが値（そのまま 1 つのキーの値にする）。
# shellcheck disable=SC2016
POD_PUT='
p="__REL__"; k="__KEY__"; over="__OVER__"
v="$(cat; printf x)"; v="${v%x}"
[ -n "$v" ] || { echo "ERROR: 値が空（何も書かない）" >&2; exit 2; }
# パスが在れば常に patch（ほかのキーを残す）。put はパスが「無い」と OpenBao が明示したときだけ。読めなければ書かずに止める。
case "$(path_state "$p")" in
  present)
    case "$(key_state "$p" "$k")" in
      present) if [ "$over" != 1 ]; then unset v; echo "ERROR: cloud/$p の $k は在る。上書きしない（上書きは --overwrite）" >&2; exit 5; fi ;;
      absent) ;;
      *) unset v; echo "ERROR: cloud/$p の $k の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;;
    esac
    op=patch ;;
  absent) op=put ;;
  *) unset v; echo "ERROR: cloud/$p の有無を読めない（権限・通信・封印）。書かずに止める" >&2; exit 1 ;;
esac
printf "%s" "$v" | b kv "$op" -mount=cloud "$p" "$k=-" >/dev/null
unset v
echo "    書いた: cloud/$p の $k（値は表示しない）"
'
POD_PUT="${POD_PUT/__REL__/$REL}"; POD_PUT="${POD_PUT/__KEY__/$K}"; POD_PUT="${POD_PUT/__OVER__/$OVERWRITE}"
# 値は手元の標準入力から、資格の 2 行の後ろへそのまま続けて Pod の標準入力へ流す（ディスクに書かない）。
{ ob_decrypt_lines "$OPERATOR_FILE" "$IDENTITY" role_id secret_id; cat; } | ob_exec "${OB_POD_LOGIN}${OB_POD_KV_STATE}${POD_PUT}"
echo "OK: ${P} の ${K} を書いた（値は表示しない）。ESO が写すまで待つか、ExternalSecret に force-sync の注釈を付ける"
