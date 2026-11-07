#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0702: Postgres の超利用者のパスワードを、OpenBao で作り直した値へ合わせる（オーナーが手元から。kubectl は SSH のトンネル越し）。
#
#   bash deploy/cloud/db-users/rotate-superuser.sh --live check   # 読むだけ（0＝合っている・3＝合わせる前・4＝どちらの値でも入れない）
#   bash deploy/cloud/db-users/rotate-superuser.sh --live apply   # 合わせる（Pod の中のソケットで ALTER ROLE。Postgres は作り直さない）
#
# 前に済ませること（手順書 docs/operations/cloud-credential-rotation-runbook.md §超利用者）:
#   1. seed-secrets.sh --live … rotate cloud/platform/postgres password（OpenBao の中で作り直す）
#   2. ExternalSecret postgres を force-sync し、check が 3 を返すまで待つ（kubelet が Pod のファイルを書き換えるまで 1〜2 分）
# 🔴 値は手元へ来ない。手順（superuser-password.sh）は標準入力で Pod へ渡し、値は Pod の中のファイルと env からだけ読む。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

MODE="${1:-}"
case "$MODE" in
  check | apply) ;;
  *) echo "ERROR: 段を与える: check（読むだけ）か apply（合わせる）" >&2; exit 2 ;;
esac
[ "$#" -eq 1 ] || { echo "ERROR: 引数は段の名前 1 つだけ" >&2; exit 2; }
NS="${POSTGRES_NS:-platform-infra}"
live_opt_in_require "rotate-superuser.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }

echo "==> 超利用者のパスワードを Secret の値と突き合わせる（段: ${MODE}。値は表示しない）"
set +e
kubectl -n "$NS" exec -i deploy/postgres -c postgres -- sh -s -- "$MODE" < "$HERE/superuser-password.sh"
rc=$?
set -e
case "$rc" in
  0) echo "OK: ${MODE} を終えた" ;;
  3) echo "==> 合わせる前（apply を流す）"; exit 3 ;;
  4) echo "ERROR: どちらの値でも入れない（手順書 §超利用者・戻し方。OpenBao の前の版へ戻してから流し直す）" >&2; exit 4 ;;
  *) echo "ERROR: Pod の中の手順が失敗した（${rc}）" >&2; exit 1 ;;
esac
