#!/usr/bin/env bash
# NFR-18, IADR-0585（Refs GHSA-j4qv-2w99-xg5p）: 秘匿管理の kubernetes 認証の role の書き込みを、配備の 2 段に分けるための口。
# `source` して使う。呼び出し元は deploy/local/vault/eso/bootstrap.sh と deploy/local/platform-backup/vault/bootstrap.sh。
#
#   vault_role_write_cmd '<vault write auth/kubernetes/role/<r> … audience=vault …>'
#     … 渡したコマンド文字列を返す。既定はそのまま（role は audience=vault を要求する）。
#       `VAULT_ROLE_AUDIENCE_DEFER=1` のときだけ、引数 `audience=vault` を 1 つ除いて返す。
#
# 役割（IADR-0585 決定 4・「結果・残る制約」）: role の書き込みは与えないフィールドを保つので、除けば **role の audience は書き換わらない**
#   （既存の値のまま）。1 段目は名乗る側（chart・store・CronJob）だけを新しくし、確かめてから 2 段目（この変数なし）で role に付ける。
# 🔴 外す（空にする）手段ではない。既に audience の付いた role は、この変数を付けて再実行しても audience を保つ。
#    値は `1` のときだけ効く（それ以外・未設定は既定。打ち間違いで黙って緩めない）。新しく立てるクラスタでは使わない。
# 呼び出し側の字面は `audience=vault` を持ったまま（scripts/vault-k8s-auth-audience.test.js と .NET の試験が字面で固定する）。
if [ "${VAULT_ROLE_AUDIENCE_DEFER:-}" = "1" ]; then
  echo "WARN: VAULT_ROLE_AUDIENCE_DEFER=1: k8s auth の role の audience を書き換えない（配備の 1 段目。確かめた後にこの変数なしで再実行する）" >&2
fi
vault_role_write_cmd() {
  if [ "${VAULT_ROLE_AUDIENCE_DEFER:-}" = "1" ]; then
    local s=" $1 "
    s="${s/ audience=vault / }"
    s="${s# }"
    printf '%s' "${s% }"
  else
    printf '%s' "$1"
  fi
}
