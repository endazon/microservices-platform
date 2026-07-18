#!/usr/bin/env bash
# IADR-0066: MSP イメージをローカルビルドし、k3s へ供給する（Harbor 不使用）。
# ランタイム自動判定（Docker Desktop も Rancher Desktop も可）:
#   - Rancher Desktop / containerd（nerdctl 有）: k8s.io namespace へ直接ビルド（import 不要）。
#   - Docker Desktop 等 / docker + k3d: docker build → k3d image import。
# タグ規則は values-local.yaml と一致: k3d-local/<chart-image>:latest（IfNotPresent で pull しない）。
#
#   bash scripts/k8s-local-images.sh [cluster-name]      # cluster-name は k3d 経路でのみ使用
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
#
# エントリ書式（IADR-0070・#283 で拡張・後方互換）:
#   2 フィールド: "image|dockerfile"                      … context=リポルート(.)・dockerfile はルート相対・args なし（従来）
#   4 フィールド: "image|context|dockerfile|k=v,k=v"      … context 指定・dockerfile は context 相対・build args 付き
# 4 フィールドは AST のような「単一パラメータ化 Dockerfile＋ユニットルート context」を載せるためのもの。
# compose の build.context/dockerfile/args と #275 ドリフト検査（check-image-mapping.js）が突合する。
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
  # Issue #283, IADR-0070: AST 設定画面の ConfigurationService。単一 Dockerfile＋build args＋ユニットルート context。
  "microservices-platform/configuration-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/ConfigurationService/src/ConfigurationService.Worker/ConfigurationService.Worker.csproj,SERVICE_DLL=ConfigurationService.Worker.dll"
  # Issue #287, IADR-0071: AST リスク設定/統制状態の RiskManagementService。同型（単一 Dockerfile＋build args＋context）。
  "microservices-platform/risk-management-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/RiskManagementService/src/RiskManagementService.Worker/RiskManagementService.Worker.csproj,SERVICE_DLL=RiskManagementService.Worker.dll"
  # Issue #288, IADR-0072: AST 監視銘柄（watchlist）の MarketMonitorService。同型（単一 Dockerfile＋build args＋context）。
  "microservices-platform/market-monitor-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/MarketMonitorService/src/MarketMonitorService.Worker/MarketMonitorService.Worker.csproj,SERVICE_DLL=MarketMonitorService.Worker.dll"
)

k3d_images=()
for entry in "${MAPPING[@]}"; do
  # エントリを最大 4 フィールドへ分解する（2 フィールド時は f3/f4 が空文字）。
  IFS='|' read -r image f2 f3 f4 <<< "$entry" || true
  if [ -n "$f3" ]; then
    context="$f2"; dockerfile="${f2%/}/${f3}"; args_csv="$f4"
  else
    context="."; dockerfile="$f2"; args_csv=""
  fi
  # build args（カンマ区切りの k=v）を --build-arg 群へ展開する。値にカンマは含めない前提。
  build_args=()
  if [ -n "$args_csv" ]; then
    IFS=',' read -ra _pairs <<< "$args_csv"
    for p in "${_pairs[@]}"; do build_args+=(--build-arg "$p"); done
  fi
  ref="${PREFIX}/${image}:${TAG}"
  echo "==> build ${ref}  (-f ${dockerfile}  context=${context}${args_csv:+  args=${args_csv}})"
  if [ "$RUNTIME" = "rancher" ]; then
    # containerd の k8s.io namespace へ直接ビルド → k3s が即参照可能（import 不要）。
    nerdctl --namespace k8s.io build -f "${dockerfile}" "${build_args[@]}" -t "${ref}" "${context}"
  else
    docker build -f "${dockerfile}" "${build_args[@]}" -t "${ref}" "${context}"
    k3d_images+=("${ref}")
  fi
done

if [ "$RUNTIME" = "k3d" ]; then
  echo "==> k3d image import (${#k3d_images[@]}) -> cluster ${CLUSTER}"
  k3d image import "${k3d_images[@]}" -c "${CLUSTER}"
fi
echo "done."
