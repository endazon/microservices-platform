#!/usr/bin/env bash
# NFR-18, IADR-0703 決定 2: オーナーの手元に、クラウドのクラスタの印（名前空間 kube-system の UID）を記録する・確かめる。
#
#   bash deploy/cloud/cluster-mark.sh --live record   # 初回（と、クラスタを作り直したとき）。印を CLOUD_CLUSTER_MARK_FILE へ書く（在れば止める）
#   bash deploy/cloud/cluster-mark.sh --live check    # 今の context が、記録したクラウドのクラスタか（deploy/cloud の手順は流す前に同じ確かめをする）
#
# record は、今の context の API の宛先がクラウドのノードへのトンネル（CLOUD_API_SERVER。既定 https://127.0.0.1:6443）であることを
# 確かめてから書く。トンネルの先がクラウドのノードであることは、record の前にオーナーが SSH の接続先で確かめる（手順書）。
# 印は秘密ではない（クラスタの識別子）。読むのは kube-system の UID だけで、何も変えない。
# 終了コード: 0＝書いた／一致 / 2＝引数の誤り・記録が在る / 3＝--live が無い / 4＝宛先が違う・読めない
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/lib/target-cluster.sh
. "$HERE/lib/target-cluster.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

CMD="${1:-}"
case "$CMD" in
  record | check) ;;
  *) echo "ERROR: record か check を与える" >&2; exit 2 ;;
esac
[ "$#" -eq 1 ] || { echo "ERROR: 引数は record か check の 1 つだけ" >&2; exit 2; }
live_opt_in_require "cluster-mark.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }

if [ "$CMD" = check ]; then
  ct_require_target "cluster-mark.sh"
  echo "OK: 今の context は、記録したクラウドのクラスタを向いている。"
  exit 0
fi

[ ! -e "$CLOUD_CLUSTER_MARK_FILE" ] \
  || { echo "ERROR: ${CLOUD_CLUSTER_MARK_FILE} は在る。上書きしない（クラスタを作り直したときは、古い記録を退避してから record する）" >&2; exit 2; }
ct_observe || ct_fail "cluster-mark.sh" "kubectl の現在の context が無い"
[ "$CT_SERVER" = "$CLOUD_API_SERVER" ] \
  || ct_fail "cluster-mark.sh" "context ${CT_CONTEXT} の API の宛先（${CT_SERVER:-（読めない）}）が、クラウドのノードへのトンネル（${CLOUD_API_SERVER}）でない"
ct_uid_shape "$CT_UID" || ct_fail "cluster-mark.sh" "context ${CT_CONTEXT} のクラスタの印を読めない（トンネルが張られているか確かめる）"
( umask 077
  mkdir -p "$(dirname "$CLOUD_CLUSTER_MARK_FILE")"
  printf '%s\n' "$CT_UID" > "$CLOUD_CLUSTER_MARK_FILE" )
echo "OK: context ${CT_CONTEXT}（${CT_SERVER}）のクラスタの印 ${CT_UID} を ${CLOUD_CLUSTER_MARK_FILE} に記録した。"
echo "    印をノードで照らす: ssh <ノード> 'sudo k3s kubectl get namespace kube-system -o jsonpath={.metadata.uid}' と同じであること（手順書）。"
