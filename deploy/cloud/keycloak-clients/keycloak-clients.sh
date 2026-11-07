#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0700: 稼働中の Keycloak の realm に、取り込み元（Secret keycloak-realm-import）に在って稼働に無い機密クライアントだけを作る
# Job を流す（オーナーが手元から。kubectl は SSH のトンネル越し）。
#
#   bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live check   # 読むだけ: 無いクライアント・足りないロールを名指す（在れば 3 で終わる）
#   bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live apply   # 無いクライアントを作り、サービスアカウントに無いロールを足す（在るものは触らない）
#   bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live sync-secrets
#       # NFR-18, IADR-0706: 在る機密クライアントの secret を取り込み元の値（OpenBao で作り直した値）へ合わせる（違うものだけ。手順書
#       #   docs/operations/cloud-credential-rotation-runbook.md）
#
# 手順の中身は add-clients.js（ConfigMap keycloak-add-clients に入れる）。ここは ConfigMap と Job を作り、終わるまで待ち、ログを出し、成功したら消すだけ。
# 🔴 値（secret・管理者のパスワード）は手元へ来ない（Job の Pod が Secret から読む）。引数に載るのは段の名前だけ。
# 前提: seed-secrets.sh が cloud/shared/ast-client-secrets に新しい clientId のキーを足し、ESO が keycloak-realm-import を作り直している。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

STAGE="${1:-}"
case "$STAGE" in
  apply | check | sync-secrets) ;;
  *) echo "ERROR: 段を与える: check（読むだけ）・apply（無いクライアントを作る）・sync-secrets（secret を取り込み元へ合わせる）" >&2; exit 2 ;;
esac
[ "$#" -eq 1 ] || { echo "ERROR: 引数は段の名前 1 つだけ" >&2; exit 2; }
NS="${KEYCLOAK_CLIENTS_NS:-platform-infra}"
JOB=keycloak-add-clients
TIMEOUT_S="${KEYCLOAK_CLIENTS_TIMEOUT_S:-300}"
case "$TIMEOUT_S" in '' | *[!0-9]*) echo "ERROR: KEYCLOAK_CLIENTS_TIMEOUT_S は秒数（正の整数）" >&2; exit 2 ;; esac
live_opt_in_require "keycloak-clients.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }
# NFR-18, IADR-0703 決定 2: 宛先のクラスタを確かめ、以後の kubectl をその context に固定する。
# shellcheck source=deploy/cloud/lib/target-cluster.sh
. "$ROOT/deploy/cloud/lib/target-cluster.sh"
ct_require_target "keycloak-clients.sh"

# 段を差し替えた Job の宣言（印の行は 1 つだけ。在るはずの行が無ければ止める）
manifest="$(awk -v stage="$STAGE" 'prev ~ /^            - name: MODE$/ && $0 ~ /^              value: "check"$/ { sub(/"check"/, "\"" stage "\"") } { print; prev = $0 }' "$HERE/job.yaml")"
[ "$(grep -c "^              value: \"${STAGE}\"$" <<< "$manifest")" -eq 1 ] || { echo "ERROR: job.yaml の段の行（MODE）を差し替えられない" >&2; exit 1; }

kubectl -n "$NS" get secret keycloak-realm-import >/dev/null || { echo "ERROR: Secret ${NS}/keycloak-realm-import が無い（先に ESO の同期）" >&2; exit 1; }
echo "==> ConfigMap ${NS}/keycloak-add-clients を作り直す（手順だけ。値を持たない）"
kubectl -n "$NS" create configmap keycloak-add-clients --from-file=add-clients.js="$HERE/add-clients.js" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
echo "==> 前の回の Job を消す（在れば）"
kubectl -n "$NS" delete job "$JOB" --ignore-not-found --wait=true >/dev/null
echo "==> Job ${NS}/${JOB} を作る（段: ${STAGE}）"
kubectl -n "$NS" create -f - <<< "$manifest" >/dev/null

# 成功（succeeded=1）か失敗（failed>=1）まで待つ。
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
echo "==> Job のログ（値は出さない。クライアントとロールの名前だけ）"
kubectl -n "$NS" logs "job/$JOB" --tail=200 || true
case "$state" in
  ok)
    kubectl -n "$NS" delete job "$JOB" --wait=false >/dev/null
    echo "OK: ${STAGE} を終えた"
    ;;
  failed) echo "ERROR: Job が失敗した（check で無いものが在れば失敗として残る。Job は残してある。ログの理由を直して流し直す。手順は冪等）" >&2; exit 1 ;;
  *) echo "ERROR: ${TIMEOUT_S} 秒で終わらない（Job は残してある。kubectl -n ${NS} describe job ${JOB}）" >&2; exit 1 ;;
esac
