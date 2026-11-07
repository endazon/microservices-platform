#!/usr/bin/env bash
# NFR-18, IADR-0703: クラウドの Keycloak の master realm の一時管理者を、要る間だけ置く（オーナーが手元から。kubectl は SSH のトンネル越し）。
#
#   bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live open    # 開く: Deployment に KC_BOOTSTRAP_ADMIN_* を足して作り直す（master に管理者が
#                                                                      #   居なければ Keycloak が Secret keycloak-admin の資格で作る）→ 総当たりの検知を有効にする
#   bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live close   # 閉じる: 総当たりの検知を確かめ、一時管理者を消す → Deployment から外して作り直す → 在らないことを確かめる
#   bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live check   # 読むだけ: Deployment に KC_BOOTSTRAP_ADMIN_* が無く、一時管理者の資格でトークンを取れないこと
#
# 🔴 open と close は Keycloak を作り直す（Recreate。トークンの発行が起動の間だけ止まる）。米国の場の時間外に流す。open の後は必ず close まで流す。
# 🔴 値（管理者のパスワード）は手元へ来ない。kubectl set env --from=secret は Secret への参照（secretKeyRef）を足すだけで、値を読まない。
# 手順の中身は master-admin.js（ConfigMap keycloak-master-admin に入れる）。手順書: docs/operations/cloud-keycloak-admin-runbook.md
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

STAGE="${1:-}"
case "$STAGE" in
  open | close | check) ;;
  *) echo "ERROR: 段を与える: open（開く）・close（閉じる）・check（読むだけ）" >&2; exit 2 ;;
esac
[ "$#" -eq 1 ] || { echo "ERROR: 引数は段の名前 1 つだけ" >&2; exit 2; }
NS="${KEYCLOAK_ADMIN_NS:-platform-infra}"
JOB=keycloak-master-admin
TIMEOUT_S="${KEYCLOAK_ADMIN_TIMEOUT_S:-300}"
case "$TIMEOUT_S" in '' | *[!0-9]*) echo "ERROR: KEYCLOAK_ADMIN_TIMEOUT_S は秒数（正の整数）" >&2; exit 2 ;; esac
live_opt_in_require "keycloak-admin.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }

# Deployment が KC_BOOTSTRAP_ADMIN_* を持つか（名前だけを読む。値は secretKeyRef なので持たない）
bootstrap_env_names() {
  kubectl -n "$NS" get deploy keycloak -o jsonpath='{range .spec.template.spec.containers[?(@.name=="keycloak")].env[*]}{.name}{"\n"}{end}' | grep -E '^KC_BOOTSTRAP_ADMIN_' || true
}

# run_job <MODE> —— 段を差し替えた Job を作り、終わるまで待ち、ログを出す。終了コードを返す（成功なら Job を消す）。
run_job() {
  local mode="$1" manifest st state="" deadline
  manifest="$(awk -v stage="$mode" 'prev ~ /^            - name: MODE$/ && $0 ~ /^              value: "check"$/ { sub(/"check"/, "\"" stage "\"") } { print; prev = $0 }' "$HERE/job.yaml")"
  [ "$(grep -c "^              value: \"${mode}\"$" <<< "$manifest")" -eq 1 ] || { echo "ERROR: job.yaml の段の行（MODE）を差し替えられない" >&2; return 1; }
  kubectl -n "$NS" create configmap keycloak-master-admin --from-file=master-admin.js="$HERE/master-admin.js" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  kubectl -n "$NS" delete job "$JOB" --ignore-not-found --wait=true >/dev/null
  echo "==> Job ${NS}/${JOB} を作る（段: ${mode}）"
  kubectl -n "$NS" create -f - <<< "$manifest" >/dev/null
  deadline=$(( $(date +%s) + TIMEOUT_S ))
  while :; do
    st="$(kubectl -n "$NS" get job "$JOB" -o jsonpath='{.status.succeeded}/{.status.failed}')"
    case "$st" in
      1/*) state=ok; break ;;
      */[1-9]*) state=failed; break ;;
    esac
    [ "$(date +%s)" -lt "$deadline" ] || { state=timeout; break; }
    sleep 3
  done
  kubectl -n "$NS" logs "job/$JOB" --tail=50 || true
  case "$state" in
    ok) kubectl -n "$NS" delete job "$JOB" --wait=false >/dev/null; return 0 ;;
    failed) echo "ERROR: Job が失敗した（Job は残してある。ログの理由を直して流し直す）" >&2; return 1 ;;
    *) echo "ERROR: ${TIMEOUT_S} 秒で終わらない（kubectl -n ${NS} describe job ${JOB}）" >&2; return 1 ;;
  esac
}

restart_wait() {
  kubectl -n "$NS" rollout status deploy/keycloak --timeout=600s
  kubectl -n "$NS" wait --for=condition=Ready pod -l app=keycloak --timeout=600s >/dev/null
}

case "$STAGE" in
  open)
    kubectl -n "$NS" get secret keycloak-admin >/dev/null || { echo "ERROR: Secret ${NS}/keycloak-admin が無い（ESO の同期）" >&2; exit 1; }
    if [ -z "$(bootstrap_env_names)" ]; then
      echo "==> Deployment keycloak に KC_BOOTSTRAP_ADMIN_* を足す（Secret keycloak-admin への参照だけ）→ 作り直す"
      kubectl -n "$NS" set env deploy/keycloak --from=secret/keycloak-admin --keys=username,password --prefix=KC_BOOTSTRAP_ADMIN_ >/dev/null
    fi
    restart_wait
    run_job harden
    echo "OK: 開いた。作業の後に必ず close を流す"
    ;;
  close)
    run_job retire
    if [ -n "$(bootstrap_env_names)" ]; then
      echo "==> Deployment keycloak から KC_BOOTSTRAP_ADMIN_* を外す → 作り直す"
      kubectl -n "$NS" set env deploy/keycloak KC_BOOTSTRAP_ADMIN_USERNAME- KC_BOOTSTRAP_ADMIN_PASSWORD- >/dev/null
      restart_wait
    fi
    run_job check
    echo "OK: 閉じた"
    ;;
  check)
    names="$(bootstrap_env_names)"
    [ -z "$names" ] || { echo "NG: Deployment keycloak が KC_BOOTSTRAP_ADMIN_* を持つ（close を流す）" >&2; exit 3; }
    rc=0; run_job check || rc=$?
    [ "$rc" -eq 0 ] || exit 3
    echo "OK: 一時管理者は在らず、Deployment も持たない"
    ;;
esac
