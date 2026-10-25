# shellcheck shell=bash
# NFR-18, IADR-0586 (GHSA-j4qv-2w99-xg5p): 管理ツールの集約入口（admin entrypoint 50000）の経路は**既定で置かない**。
#   ADMIN_EDGE=1 を明示したときだけ、grafana / headlamp / vault / qdrant / wiki / argocd の 6 host の経路を置く。
#   未指定・0 では経路を消す（以前の実行で置いた経路も消す＝再実行で閉じる）。入口（50000 の listener）自体は残る:
#     - Traefik … 該当する router が無いので 404。
#     - Istio   … msp-admin-deny（*.localhost の directResponse 403。deploy/local/edge-istio/virtualservice-admin-deny.yaml）。
#   管理者の既定の入り方は各ツールへの kubectl port-forward（deploy/local/edge/README.md「管理ツールへの入り方」）。
#
# 読み手: scripts/k8s-local-up.sh（Traefik）・scripts/istio-edge-up.sh（Istio）・scripts/istio-edge-down.sh（Traefik へ戻す）。
# 呼び出し側はリポジトリのルートを cwd にしていること（パスは相対で書く）。

# 0 / 1 / 未指定 以外を拒む（打ち間違いを黙って「閉じる」とも「開ける」とも読まない）。副作用より前に呼ぶ。
admin_edge_validate() {
  case "${ADMIN_EDGE:-}" in
    ''|0|1) return 0 ;;
    *)
      echo "ERROR: ADMIN_EDGE は 1（管理ツールの経路を 50000 に置く）・0 か未指定（置かない。既定）のどちらかです: '${ADMIN_EDGE}'" >&2
      return 1
      ;;
  esac
}

# Traefik の admin entrypoint の経路（3 ファイル・6 Ingress）を ADMIN_EDGE に合わせて置く／消す。
admin_edge_traefik() {
  local f files=(deploy/local/edge/admin-ingress-infra.yaml deploy/local/edge/admin-ingress-wiki.yaml)
  # argocd namespace は ARGOCD=1 の別 opt-in でしか作られない（無い ns へは当てない・消さない）。
  if kubectl get namespace argocd >/dev/null 2>&1; then
    files+=(deploy/local/edge/argocd-ingress.yaml)
  fi
  if [ "${ADMIN_EDGE:-}" = "1" ]; then
    echo "    ADMIN_EDGE=1: 管理ツールの経路を admin(50000) に置く（明示の opt-in。IADR-0586）"
    for f in "${files[@]}"; do kubectl apply -f "$f"; done
  else
    echo "    ADMIN_EDGE が 1 でない（既定）: 管理ツールの経路を admin(50000) に置かない（既定。入り方は port-forward。IADR-0586）"
    for f in "${files[@]}"; do kubectl delete -f "$f" --ignore-not-found=true; done
  fi
}

# Istio の msp-admin-edge（50000）の経路（6 VirtualService）を ADMIN_EDGE に合わせて置く／消す。
# 既定の拒否（msp-admin-deny）は deploy/local/edge-istio の kustomization が常に置く（host が具体的な経路が優先される）。
admin_edge_istio() {
  if [ "${ADMIN_EDGE:-}" = "1" ]; then
    echo "    ADMIN_EDGE=1: 管理ツールの経路を msp-admin-edge(50000) に置く（明示の opt-in。IADR-0586）"
    kubectl apply -f deploy/local/edge-istio/virtualservice-admin.yaml
  else
    echo "    ADMIN_EDGE が 1 でない（既定）: 管理ツールの経路を msp-admin-edge(50000) に置かない（既定は 403。入り方は port-forward。IADR-0586）"
    kubectl delete -f deploy/local/edge-istio/virtualservice-admin.yaml --ignore-not-found=true
  fi
}
