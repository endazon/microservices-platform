# shellcheck shell=bash
# NFR-18, NFR-05, IADR-0703 決定 2: オーナーが手元から流す deploy/cloud の手順（vault/*.sh・keycloak-clients・keycloak-admin・db-users）が、
# **宛先のクラスタを確かめてから** kubectl を使うための関数。
#
#   . "$ROOT/deploy/cloud/lib/target-cluster.sh"
#   ct_require_target "init.sh"      # live_opt_in_require と kubectl の有無の確かめの後、最初の kubectl より前に置く
#
# 確かめること（どれかが違えば、何もせずに 4 で終わる）:
#   1. kubectl の現在の context が在る。
#   2. その context の API の宛先が、クラウドのノードへの SSH のトンネル（既定 https://127.0.0.1:6443。CLOUD_API_SERVER で変える）である。
#   3. その宛先のクラスタの印（名前空間 kube-system の UID）が、手元に記録した印（CLOUD_CLUSTER_MARK_FILE。cluster-mark.sh record が書く）と同じである。
# 確かめた後は、この手順の中の kubectl を、確かめた context に固定する（途中で現在の context が替わっても、別のクラスタへ流れない）。
# 印は秘密ではない（クラスタの識別子）。値は扱わない。

CT_EXIT=4
CLOUD_API_SERVER="${CLOUD_API_SERVER:-https://127.0.0.1:6443}"
CLOUD_CLUSTER_MARK_FILE="${CLOUD_CLUSTER_MARK_FILE:-${XDG_CONFIG_HOME:-$HOME/.config}/microservices-platform/cloud-cluster-mark}"

ct_fail() {
  echo "ERROR: [$1] 宛先のクラスタを確かめられない: $2（何もしていない）" >&2
  exit "$CT_EXIT"
}

# ct_observe —— 現在の context・API の宛先・クラスタの印を読み、CT_CONTEXT・CT_SERVER・CT_UID に入れる（command kubectl で読む）。
ct_observe() {
  CT_CONTEXT="$(command kubectl config current-context 2>/dev/null || true)"
  [ -n "$CT_CONTEXT" ] || return 1
  CT_SERVER="$(command kubectl config view --minify --context "$CT_CONTEXT" -o 'jsonpath={.clusters[0].cluster.server}' 2>/dev/null || true)"
  CT_UID="$(command kubectl --context "$CT_CONTEXT" get namespace kube-system -o 'jsonpath={.metadata.uid}' 2>/dev/null || true)"
  return 0
}

ct_uid_shape() { [[ "$1" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]; }

# ct_require_target <手順の名前>
ct_require_target() {
  local name="$1" mark
  ct_observe || ct_fail "$name" "kubectl の現在の context が無い"
  [ "$CT_SERVER" = "$CLOUD_API_SERVER" ] \
    || ct_fail "$name" "context ${CT_CONTEXT} の API の宛先（${CT_SERVER:-（読めない）}）が、クラウドのノードへのトンネル（${CLOUD_API_SERVER}）でない"
  ct_uid_shape "$CT_UID" || ct_fail "$name" "context ${CT_CONTEXT} のクラスタの印（kube-system の UID）を読めない（トンネルが張られているか確かめる）"
  [ -r "$CLOUD_CLUSTER_MARK_FILE" ] \
    || ct_fail "$name" "手元に記録したクラスタの印が無い: ${CLOUD_CLUSTER_MARK_FILE}（初回は bash deploy/cloud/cluster-mark.sh --live record。手順書 cloud-owner-target-cluster-runbook.md）"
  mark="$(head -n 1 "$CLOUD_CLUSTER_MARK_FILE")"
  ct_uid_shape "$mark" || ct_fail "$name" "記録したクラスタの印の形が違う: ${CLOUD_CLUSTER_MARK_FILE}"
  [ "$mark" = "$CT_UID" ] \
    || ct_fail "$name" "context ${CT_CONTEXT} のクラスタ（印 ${CT_UID}）は、記録したクラウドのクラスタ（印 ${mark}）でない"
  # 以後の kubectl を、確かめた context に固定する（この手順のシェルと、その中の $(…)・パイプに効く）。
  # shellcheck disable=SC2329
  kubectl() { command kubectl --context "$CT_CONTEXT" "$@"; }
  echo "==> 宛先: context ${CT_CONTEXT}（${CT_SERVER}・クラスタの印は記録と一致）" >&2
}
