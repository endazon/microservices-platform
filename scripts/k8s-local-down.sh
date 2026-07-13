#!/usr/bin/env bash
# IADR-0066: ローカル k8s(k3d) dev 環境の破棄。
#   scripts/k8s-local-down.sh [cluster-name]     # 既定 msp-ast-dev
set -euo pipefail
CLUSTER="${1:-msp-ast-dev}"
if k3d cluster list "$CLUSTER" >/dev/null 2>&1; then
  k3d cluster delete "$CLUSTER"
  echo "deleted cluster '$CLUSTER'."
else
  echo "cluster '$CLUSTER' not found — nothing to do."
fi
