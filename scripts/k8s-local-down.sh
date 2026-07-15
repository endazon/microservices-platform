#!/usr/bin/env bash
# IADR-0066: ローカル k8s dev 環境の破棄。
#   bash scripts/k8s-local-down.sh [cluster-name]     # 既定 msp-ast-dev
# k3d 経路はクラスタごと削除。Rancher Desktop（内蔵 k3s）経路は release/namespace のみ撤去
# （内蔵クラスタ自体は消さない）。
set -euo pipefail
CLUSTER="${1:-msp-ast-dev}"

RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher"; else RUNTIME="k3d"; fi
fi

if [ "$RUNTIME" = "k3d" ] && command -v k3d >/dev/null 2>&1 && k3d cluster list "$CLUSTER" >/dev/null 2>&1; then
  k3d cluster delete "$CLUSTER"
  echo "deleted k3d cluster '$CLUSTER'."
else
  echo "Rancher Desktop 経路: helm release と namespace を撤去します（内蔵 k3s は残す）。"
  helm uninstall msp -n microservices-platform 2>/dev/null || true
  helm uninstall ast -n ai-stock-trading 2>/dev/null || true
  kubectl delete namespace microservices-platform ai-stock-trading platform-infra --ignore-not-found
  echo "done."
fi
