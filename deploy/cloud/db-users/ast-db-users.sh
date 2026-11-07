#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0695: 取引ユニットの DB の利用者をサービスごとに分ける Job を流す（オーナーが手元から。kubectl は SSH のトンネル越し）。
#
#   bash deploy/cloud/db-users/ast-db-users.sh --live apply        # 段 ①（と新しく作る構成）: 7 人を作り、各 DB の OWNER を移し、他の DB への CONNECT を外す
#   bash deploy/cloud/db-users/ast-db-users.sh --live drop-shared  # 段 ③（最終）: 共有の利用者の接続が無いことを確かめて消す
#   bash deploy/cloud/db-users/ast-db-users.sh --live platform     # IADR-0702: バックアップの利用者を作り（在ればパスワードを合わせ）、
#                                                                  #   Keycloak の DB の利用者のパスワードを Secret の値へ合わせる
#
# NFR-18, IADR-0702: Postgres は超利用者の TCP を既定で拒む。Job の前にゲートを開け（Pod の中の superuser-tcp-gate.sh）、
#   Job が終われば（失敗・時間切れ・中断でも）閉じる。閉じたことを確かめて終わる。
#
# 手順の中身（SQL）は ConfigMap postgres-ast-db-users（deploy/cloud/infra/postgres-ast-db-users.yaml）。ここは Job を作り、終わるまで待ち、
# ログを出し、成功したら消すだけ（失敗したら調べられるよう残す。ttlSecondsAfterFinished で 1 日の後に消える）。
# 🔴 値（利用者名・パスワード）は手元へ来ない（Job の Pod が Secret から読む）。引数に載るのは段の名前だけ。
# 手順は docs/operations/cloud-db-per-service-users-runbook.md。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

STAGE="${1:-}"
case "$STAGE" in
  apply | drop-shared | platform) ;;
  *) echo "ERROR: 段を与える: apply（段 ①）・drop-shared（段 ③）・platform（バックアップと Keycloak の DB の利用者）" >&2; exit 2 ;;
esac
[ "$#" -eq 1 ] || { echo "ERROR: 引数は段の名前 1 つだけ" >&2; exit 2; }
NS="${AST_DB_USERS_NS:-platform-infra}"
JOB=postgres-ast-db-users
TIMEOUT_S="${AST_DB_USERS_TIMEOUT_S:-600}"
case "$TIMEOUT_S" in '' | *[!0-9]*) echo "ERROR: AST_DB_USERS_TIMEOUT_S は秒数（正の整数）" >&2; exit 2 ;; esac
live_opt_in_require "ast-db-users.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }

# 段を差し替えた Job の宣言（印の行は 1 つだけ。在るはずの行が無ければ止める）
manifest="$(sed "s/^\(          args: \)\[\"apply\"\]$/\1[\"${STAGE}\"]/" "$HERE/job.yaml")"
[ "$(grep -c "^          args: \[\"${STAGE}\"\]$" <<< "$manifest")" -eq 1 ] || { echo "ERROR: job.yaml の段の行（args）を差し替えられない" >&2; exit 1; }

kubectl -n "$NS" get configmap postgres-ast-db-users >/dev/null || { echo "ERROR: ConfigMap ${NS}/postgres-ast-db-users が無い（先に kubectl apply -k deploy/cloud）" >&2; exit 1; }

# NFR-18, IADR-0702: 超利用者の TCP のゲート（Pod の中で開け閉めする。値は扱わない）。
gate() { kubectl -n "$NS" exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh "$1"; }
close_gate() {
  local rc=$?
  trap - EXIT INT TERM
  echo "==> 超利用者の TCP のゲートを閉じる"
  if ! gate close >/dev/null || [ "$(gate status)" != "superuser-tcp: closed" ]; then
    echo "ERROR: ゲートを閉じたことを確かめられない（kubectl -n ${NS} exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh close を流し直す。" \
      "届かなければ、上限（既定 900 秒）でゲートは閉じる。待てないときは kubectl -n ${NS} rollout restart deploy/postgres で閉じる。手順書 §1）" >&2
    exit 1
  fi
  exit "$rc"
}
echo "==> 超利用者の TCP のゲートを開ける（Job の間だけ）"
trap close_gate EXIT
trap 'exit 130' INT TERM
gate open >/dev/null || { echo "ERROR: ゲートを開けられない（Postgres の Pod が IADR-0702 の宣言で動いているかを確かめる）" >&2; exit 1; }
echo "==> 前の回の Job を消す（在れば）"
kubectl -n "$NS" delete job "$JOB" --ignore-not-found --wait=true >/dev/null
echo "==> Job ${NS}/${JOB} を作る（段: ${STAGE}）"
kubectl -n "$NS" create -f - <<< "$manifest" >/dev/null

# 成功（succeeded=1）か失敗（failed>=1）まで待つ（wait --for=condition=complete は失敗でも待ち続けるので、両方を見る）。
deadline=$(( $(date +%s) + TIMEOUT_S ))
state=""
while :; do
  st="$(kubectl -n "$NS" get job "$JOB" -o jsonpath='{.status.succeeded}/{.status.failed}')"
  case "$st" in
    1/*) state=ok; break ;;
    */[1-9]*) state=failed; break ;;
  esac
  [ "$(date +%s)" -lt "$deadline" ] || { state=timeout; break; }
  sleep 3
done
echo "==> Job のログ（値は出さない。サービスの名前と確かめの結果だけ）"
kubectl -n "$NS" logs "job/$JOB" --tail=200 || true
case "$state" in
  ok)
    kubectl -n "$NS" delete job "$JOB" --wait=false >/dev/null
    echo "OK: ${STAGE} を終えた"
    ;;
  failed) echo "ERROR: Job が失敗した（Job は残してある。理由を直して同じ段を流し直す。手順は冪等）" >&2; exit 1 ;;
  *) echo "ERROR: ${TIMEOUT_S} 秒で終わらない（Job は残してある。kubectl -n ${NS} describe job ${JOB}）" >&2; exit 1 ;;
esac
