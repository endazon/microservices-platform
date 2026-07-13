#!/usr/bin/env bash
# IADR-0066: MSP イメージをローカルビルドし k3d クラスタへ import する（Harbor 不使用）。
# タグ規則は values-local.yaml と一致させる: k3d-local/<chart-image>:latest（IfNotPresent で pull しない）。
#
# 使い方:
#   scripts/k8s-local-images.sh [cluster-name]     # 既定 cluster-name = msp-ast-dev
#
# 前提: docker / k3d が導入済み、対象クラスタが起動済み（scripts/k8s-local-up.sh が呼ぶ）。
set -euo pipefail

CLUSTER="${1:-msp-ast-dev}"
PREFIX="k3d-local"
TAG="latest"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

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

images=()
for entry in "${MAPPING[@]}"; do
  image="${entry%%|*}"; dockerfile="${entry##*|}"
  ref="${PREFIX}/${image}:${TAG}"
  echo "==> build ${ref}  (${dockerfile})"
  # 全 Dockerfile はリポジトリルートを build context とする（compose の context: .. と一致）。
  docker build -f "${dockerfile}" -t "${ref}" .
  images+=("${ref}")
done

echo "==> k3d image import (${#images[@]} images) -> cluster ${CLUSTER}"
k3d image import "${images[@]}" -c "${CLUSTER}"
echo "done."
