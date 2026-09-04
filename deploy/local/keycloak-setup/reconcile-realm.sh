#!/usr/bin/env bash
# FR-05, NFR-09, ADR-0004 / ADR-0026, IADR-0368 (#1088 / #324):
# realm JSON（ConfigMap keycloak-realms＝単一情報源）と稼働 realm の差分を、Job で Admin REST API から当てる。
#
#   bash deploy/local/keycloak-setup/reconcile-realm.sh            # 差分を当てる（apply）
#   bash deploy/local/keycloak-setup/reconcile-realm.sh --check    # 差分を数えるだけ（書き換えない）。1 件でも在れば exit 1
#
# ## なぜ要るか —— realm JSON を直しても既存クラスタには届かない
#
# Keycloak は `start-dev --import-realm` で立つ。**同名 realm が既に在ると import は黙って飛ばされる**
# （`IGNORE_EXISTING`）。永続化（IADR-0368: 既定オン）で realm が PVC に残るようになった瞬間から、
# `deploy/keycloak/microservices-platform-realm.json` を直しても稼働 realm は変わらない。
# 本スクリプトが後追いで差分を当てる。Wiki.js の bootstrap（IADR-0327）と同型の「冪等な再適用」である。
#
# ## 作法
#
# - **pod 内で kcadm.sh を exec しない**（別 JVM が Keycloak 本体を OOMKilled にする。2026-09-02 実測）。
#   同じ namespace の Job（node:22-alpine）が `http://keycloak:8080` を叩く。エッジ・TLS・メッシュに依存しない。
# - **冪等**: 差分が無ければ何もしない。差分の意味論（宣言所有／実行時所有の境界）は reconcile-realm.js 冒頭。
# - **期待値は ConfigMap keycloak-realms から読む**（起動器 [3/7] が実 realm ファイルから作る。ここへ二重に書かない）。
# - **秘匿値を持たない**: 管理者資格情報は Secret keycloak-admin を Job の env が secretKeyRef で読む。
#   標準出力には値を出さない（Job のログも出さない）。
# - **best-effort は呼び出し側の責務**: 本スクリプトは失敗を非 0 で返す。`k8s-local-up.sh` は WARN に落とし、
#   fail-closed の門は `scripts/check-stack-ready.js` の G9（--check）に置く。
#
# 環境変数（すべて任意）:
#   INFRA_NS                 Keycloak の namespace（既定 platform-infra）
#   RECONCILE_JOB_TIMEOUT    Job の完了待ち秒数（既定 300）
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_NS="${INFRA_NS:-platform-infra}"
TIMEOUT="${RECONCILE_JOB_TIMEOUT:-300}"
MODE="apply"
JOB="keycloak-realm-reconcile"
if [ "${1:-}" = "--check" ]; then
  MODE="check"
  JOB="keycloak-realm-check"
elif [ -n "${1:-}" ]; then
  echo "ERROR: 未知の引数: $1（受け付けるのは --check だけ）" >&2
  exit 2
fi
MANIFEST="$HERE/realm-reconcile-job.yaml"
SCRIPT="$HERE/reconcile-realm.js"

if ! kubectl -n "$INFRA_NS" get deploy/keycloak >/dev/null 2>&1; then
  echo "    Keycloak が居ないためスキップします（$INFRA_NS/keycloak）"
  # check モードでは「測れなかった」を成功にしない。
  [ "$MODE" = "check" ] && exit 1
  exit 0
fi

# 1. スクリプト本体を ConfigMap にする（毎回上書き＝リポジトリの版が正）。
kubectl create configmap keycloak-realm-reconcile -n "$INFRA_NS" \
  --from-file=reconcile-realm.js="$SCRIPT" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

# 2. 前回の Job を消す（Job は immutable。再 apply できない）。
kubectl -n "$INFRA_NS" delete job "$JOB" --ignore-not-found --wait=true >/dev/null

# 3. マニフェストの name と RECONCILE_MODE を差し替えて apply する。
#    宣言は realm-reconcile-job.yaml の 1 本だけ（check 用の写しを持たない）。
sed -e "s/^\(  name:\) keycloak-realm-reconcile\$/\1 $JOB/" \
    -e "s/^\(              value:\) \"apply\"\$/\1 \"$MODE\"/" \
    "$MANIFEST" | kubectl apply -f - >/dev/null

# 4. 完了を待つ。Complete / Failed のどちらかが立つまで見る（`kubectl wait --for=condition=complete` は
#    Failed で立つと timeout まで戻らないので使わない）。
deadline=$(( $(date +%s) + TIMEOUT ))
state=""
while [ -z "$state" ]; do
  # conditions は複数立つ（k8s 1.31+ は Failed の前に FailureTarget も立てる）。型名の集合から Complete / Failed を読む。
  conds="$(kubectl -n "$INFRA_NS" get job "$JOB" \
    -o jsonpath='{range .status.conditions[?(@.status=="True")]}{.type}{" "}{end}' 2>/dev/null || true)"
  case " $conds " in
    *" Complete "*) state="Complete" ;;
    *" Failed "*) state="Failed" ;;
  esac
  # stub 環境や古い kubectl で conditions が読めないときは、succeeded / failed の件数で見る。
  if [ -z "$state" ]; then
    counts="$(kubectl -n "$INFRA_NS" get job "$JOB" -o jsonpath='{.status.succeeded}/{.status.failed}' 2>/dev/null || true)"
    case "$counts" in
      1/*) state="Complete" ;;
      */1) state="Failed" ;;
    esac
  fi
  if [ -z "$state" ] && [ "$(date +%s)" -ge "$deadline" ]; then
    state="Timeout"
  fi
  [ -z "$state" ] && sleep 3
done

# 5. ログを出す（値は含まれない。reconcile-realm.js は body を出力しない）。
echo "    --- $JOB ($MODE) ---"
kubectl -n "$INFRA_NS" logs "job/$JOB" 2>/dev/null | sed 's/^/    /' || true

case "$state" in
  Complete)
    echo "    realm の追随: OK（$MODE）"
    ;;
  *)
    echo "    ERROR: realm の追随に失敗（$MODE / $state）。差分の中身は上のログ。" >&2
    exit 1
    ;;
esac
