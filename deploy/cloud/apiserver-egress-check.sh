#!/usr/bin/env bash
# NFR-18, IADR-0709 決定 4: クラスタの中から API サーバーへ届く宛先が、宣言で名指した番地（ESO・OpenBao・collector の出の ipBlock）と
# 同じであることを確かめる（読むだけ。何も変えない）。
#
#   bash deploy/cloud/apiserver-egress-check.sh --live
#
# 読むもの: Service default/kubernetes の ClusterIP とポート、その EndpointSlice の番地とポート。宣言の側の値は下の PINNED_* と同じ
# （scripts/deploy-cloud-egress-audit.test.js が宣言の ipBlock と突き合わせる）。
# 終了コード: 0＝一致 / 1＝違う（段 1 の宛先を問わない宣言を消さない。違いを手順書 cloud-egress-proxy-runbook §7 に沿って直す）/
#             2＝引数の誤り / 3＝--live が無い / 4＝宛先のクラスタを確かめられない
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/lib/target-cluster.sh
. "$HERE/lib/target-cluster.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
[ "$#" -eq 0 ] || { echo "ERROR: 引数は --live だけ" >&2; exit 2; }
live_opt_in_require "apiserver-egress-check.sh"
command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl が無い" >&2; exit 1; }
ct_require_target "apiserver-egress-check.sh"

# 宣言で名指した番地（Pod が出す宛先＝Service の ClusterIP:ポート、ノードで書き換わった先＝EndpointSlice の番地:ポート）。
PINNED_SERVICE="10.43.0.1:443"
PINNED_ENDPOINT="127.0.0.1:6443"

svc="$(kubectl -n default get service kubernetes -o 'jsonpath={.spec.clusterIP}:{.spec.ports[?(@.name=="https")].port}' 2>/dev/null || true)"
eps="$(kubectl -n default get endpointslices -l kubernetes.io/service-name=kubernetes \
  -o 'jsonpath={range .items[*]}{range .endpoints[*]}{range .addresses[*]}{@}{"\n"}{end}{end}{end}' 2>/dev/null | sort -u || true)"
port="$(kubectl -n default get endpointslices -l kubernetes.io/service-name=kubernetes \
  -o 'jsonpath={range .items[*]}{range .ports[?(@.name=="https")]}{.port}{"\n"}{end}{end}' 2>/dev/null | sort -u || true)"

rc=0
echo "Service default/kubernetes: ${svc:-（読めない）}（宣言: ${PINNED_SERVICE}）"
[ "$svc" = "$PINNED_SERVICE" ] || { echo "NG: Service の番地が宣言と違う"; rc=1; }
found=""
while IFS= read -r a; do
  [ -n "$a" ] || continue
  while IFS= read -r p; do
    [ -n "$p" ] || continue
    found="${found}${a}:${p} "
  done <<< "$port"
done <<< "$eps"
echo "EndpointSlice: ${found:-（読めない）}（宣言: ${PINNED_ENDPOINT}）"
[ "${found% }" = "$PINNED_ENDPOINT" ] || { echo "NG: EndpointSlice の番地が宣言と違う（1 つで、宣言と同じであること）"; rc=1; }
if [ "$rc" -eq 0 ]; then
  echo "OK: API サーバーへの宛先は宣言で名指した番地と同じ（段 1 の宛先を問わない宣言を消す前に、手順書 §5 の確かめを続ける）。"
fi
exit "$rc"
