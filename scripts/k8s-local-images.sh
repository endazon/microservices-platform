#!/usr/bin/env bash
# IADR-0066: MSP イメージをローカルビルドし、k3s へ供給する（Harbor 不使用）。
# ランタイム自動判定（Docker Desktop も Rancher Desktop も可）:
#   - Rancher Desktop / containerd（nerdctl 有）: k8s.io namespace へ直接ビルド（import 不要）。
#   - Docker Desktop 等 / docker + k3d: docker build → k3d image import。
# タグ規則は values-local.yaml と一致: k3d-local/<chart-image>:latest（IfNotPresent で pull しない）。
#
#   scripts/k8s-local-images.sh [cluster-name]      # cluster-name は k3d 経路でのみ使用
#   K8S_LOCAL_RUNTIME=rancher|k3d で明示指定も可（既定 auto）。
set -euo pipefail

CLUSTER="${1:-msp-ast-dev}"
PREFIX="k3d-local"   # 実レジストリではないローカル接頭辞（Rancher/k3d 共通）
TAG="latest"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
  elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
  else echo "ERROR: nerdctl（Rancher Desktop/containerd）か docker+k3d が必要です。" >&2; exit 1; fi
fi
echo "==> runtime: $RUNTIME"

# chart-image(=values.yaml services.<name>.image) : Dockerfile パス（compose の build と一致）
MAPPING=(
  "microservices-platform/document-service|src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Dockerfile"
  "microservices-platform/datasource-service|src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Dockerfile"
  "microservices-platform/conversion-service|src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Dockerfile"
  "microservices-platform/ingestion-service|src/knowledge/backend/Services/IngestionService/src/IngestionService.Worker/Dockerfile"
  "microservices-platform/retrieval-service|src/knowledge/backend/Services/RetrievalService/src/RetrievalService.Api/Dockerfile"
  "microservices-platform/aianalysis-service|src/knowledge/backend/Services/AiAnalysisService/src/AiAnalysisService.Api/Dockerfile"
  "microservices-platform/authorization-service|src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/Dockerfile"
  "microservices-platform/wiki-service|src/knowledge/backend/Services/WikiService/src/WikiService.Api/Dockerfile"
  "microservices-platform/llm-gateway|src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/Dockerfile"
  "microservices-platform/feedback-service|src/knowledge/backend/Services/FeedbackService/src/FeedbackService.Api/Dockerfile"
  "microservices-platform/dashboard-service|src/knowledge/backend/Services/DashboardService/src/DashboardService.Api/Dockerfile"
  "microservices-platform/bff|src/platform/backend/Bff/Platform.Bff/Dockerfile"
)

k3d_images=()
for entry in "${MAPPING[@]}"; do
  image="${entry%%|*}"; dockerfile="${entry##*|}"
  ref="${PREFIX}/${image}:${TAG}"
  echo "==> build ${ref}  (${dockerfile})"
  if [ "$RUNTIME" = "rancher" ]; then
    # containerd の k8s.io namespace へ直接ビルド → k3s が即参照可能（import 不要）。
    nerdctl --namespace k8s.io build -f "${dockerfile}" -t "${ref}" .
  else
    docker build -f "${dockerfile}" -t "${ref}" .
    k3d_images+=("${ref}")
  fi
done

if [ "$RUNTIME" = "k3d" ]; then
  echo "==> k3d image import (${#k3d_images[@]}) -> cluster ${CLUSTER}"
  k3d image import "${k3d_images[@]}" -c "${CLUSTER}"
fi
echo "done."
