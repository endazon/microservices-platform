#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 1: クラウドの秘匿管理（OpenBao）を初期化する（一度だけ。オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/init.sh --live --recipients <age の受取人のファイル> --out <書き出すファイル>.age
#
# - unseal の鍵は 1 分割・閾値 1（利用者の裁定 2026-11-06）。出力（unseal の鍵と初回の root）は、Pod の中から手元の
#   `age -R` へ**そのままパイプで流し**、暗号化したファイルだけを作る。平文はノードのディスク・手元のディスク・端末・
#   シェルの履歴のどれにも残らない（コマンド行に値は無い）。
# - 作ったファイルは、オーナーが別々の 2 か所に保管する（場所はリポジトリに書かない）。
# - 初回の root は configure.sh が 1 回だけ使って失効させる。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

RECIPIENTS="" OUT=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --recipients) RECIPIENTS="${2:-}"; shift 2 ;;
    --out) OUT="${2:-}"; shift 2 ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--recipients・--out）" >&2; exit 2 ;;
  esac
done
ob_readable "$RECIPIENTS" "age の受取人のファイル（--recipients）"
ob_fresh "$OUT"
live_opt_in_require "init.sh"
ob_need kubectl age
# 受取人で暗号化できることを先に確かめる（init の後で age が失敗すると、鍵を受け取れないまま初期化が済んでしまう）。
printf 'probe' | age -R "$RECIPIENTS" >/dev/null || { echo "ERROR: 受取人のファイルで暗号化できない（age -R を確かめる）" >&2; exit 2; }

state="$(ob_exec 'bao status -format=json 2>/dev/null || true' | tr -d '\n ')"
case "$state" in
  *'"initialized":false'*) ;;
  *'"initialized":true'*) echo "ERROR: 既に初期化してある。init は一度だけ（鍵を作り直すなら手順書の rekey）" >&2; exit 1 ;;
  *) echo "ERROR: OpenBao の状態を読めない（Pod が起動しているか確かめる: kubectl -n ${OPENBAO_NS} get pods）" >&2; exit 1 ;;
esac

# age は空の入力でも暗号化したファイルを作るので、パイプのどこかが失敗したら書き出し先を消す（壊れた鍵のファイルを残さない）。
if ! ( umask 077
       ob_exec 'bao operator init -key-shares=1 -key-threshold=1 -format=json' | age -R "$RECIPIENTS" -o "$OUT" ); then
  rm -f "$OUT"
  echo "ERROR: 初期化に失敗した（書き出し先は消した）。kubectl -n ${OPENBAO_NS} logs deploy/openbao で理由を読む" >&2
  exit 1
fi
chmod 600 "$OUT"
echo "OK: 初期化した。unseal の鍵と初回の root を age で暗号化して ${OUT} に書いた（値は表示しない）。"
echo "    次: このファイルを別々の 2 か所に保管し、unseal.sh → configure.sh の順に流す（手順書）。"
