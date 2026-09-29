#!/usr/bin/env bash
# NFR-21, IADR-0471 (#1699): platform-backup の CronJob を**前提が揃うまで止めて置く**ための判定。
# `source` して使う。k8s-local-up.sh の最後で呼ばれる。
#
# 前提は 2 つ:
#   1. image    … CronJob が使うイメージがクラスタのランタイムに在る（IfNotPresent で pull しない。無ければ ImagePullBackOff）
#   2. recipients … ConfigMap platform-backup-age-recipients の recipients.txt が、age の公開鍵だけから成る
#                   （占位・不在・不正な行なら、スクリプトは何も書かずに失敗する）
#
# 🔴 欠けたまま有効にしておくと、毎日の回が失敗した Pod を残す。Ready でない Pod として監視の雑音になり、
#    「CronJob がある＝バックアップが動いている」と誤読させる（#1699）。**揃うまで suspend で置き、揃った再実行で有効へ戻す。**
# 🔴 受取人の検査は CronJob の中で走る backup.sh の check_recipients をそのまま使う（BACKUP_LIB=1 で読み込む）。
#    ここで別の規則を書くと、門は通ったのに CronJob の中で落ちる（またはその逆）ずれを作る。

BACKUP_CRONJOB_GATE_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKUP_CRONJOB_GATE_SCRIPT="${BACKUP_CRONJOB_GATE_LIB_DIR}/../../deploy/local/platform-backup/script/backup.sh"

# backup_recipients_file_ok <file>
# 受取人ファイルが CronJob の中の検査を通るなら 0。理由の文言は捨てる（公開鍵でも中身は表示しない）。
backup_recipients_file_ok() {
  local file="${1:-}"
  [ -n "$file" ] || return 1
  (
    BACKUP_LIB=1
    # shellcheck source=/dev/null
    . "$BACKUP_CRONJOB_GATE_SCRIPT" || exit 3
    check_recipients "$file"
  ) >/dev/null 2>&1
}

# backup_cronjob_suspend <image_present:0|1> <recipients_file>
# 標準出力へ suspend の値（true / false）を 1 行、続けて欠けている前提の名前（image / recipients）を 1 行ずつ出す。
backup_cronjob_suspend() {
  local image_present="${1:-0}" recipients_file="${2:-}" missing=()
  [ "$image_present" = "1" ] || missing+=(image)
  backup_recipients_file_ok "$recipients_file" || missing+=(recipients)
  if [ "${#missing[@]}" -eq 0 ]; then
    echo false
  else
    echo true
    printf '%s\n' "${missing[@]}"
  fi
}
