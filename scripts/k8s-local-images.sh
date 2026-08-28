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
  "microservices-platform/document-service|src/knowledge/backend/Services/DocumentService/Dockerfile"
  "microservices-platform/datasource-service|src/knowledge/backend/Services/DataSourceService/Dockerfile"
  "microservices-platform/conversion-service|src/knowledge/backend/Services/ConversionService/Worker/Dockerfile"
  "microservices-platform/ingestion-service|src/knowledge/backend/Services/IngestionService/Worker/Dockerfile"
  "microservices-platform/retrieval-service|src/knowledge/backend/Services/RetrievalService/Dockerfile"
  "microservices-platform/aianalysis-service|src/knowledge/backend/Services/AiAnalysisService/Dockerfile"
  "microservices-platform/authorization-service|src/platform/backend/Services/AuthorizationService/Dockerfile"
  # FR-22, ADR-0045, IADR-0288 (#1025): 利用者通知。実装・テスト（53 件）と Dockerfile は揃っていたが
  # compose / values / MAPPING のいずれにも無く、イメージが焼かれず配備にも出ていなかった
  # （graph-service の #908/#957 と同型の欠落）。
  "microservices-platform/notification-service|src/platform/backend/Services/NotificationService/Dockerfile"
  # FR-16, UC-09, SC-12, ADR-0024 (#452): MCP サーバー。実装・テストは #445 で着地していたが
  # Dockerfile も compose / values / MAPPING も無く、イメージが焼かれず配備にも出ていなかった
  # （notification-service の #1025 と同型の欠落）。
  "microservices-platform/mcp-service|src/platform/backend/Services/McpServer/Dockerfile"
  "microservices-platform/wiki-service|src/knowledge/backend/Services/WikiService/Dockerfile"
  "microservices-platform/llm-gateway|src/platform/backend/Services/LlmGateway/Dockerfile"
  "microservices-platform/feedback-service|src/knowledge/backend/Services/FeedbackService/Dockerfile"
  "microservices-platform/dashboard-service|src/knowledge/backend/Services/DashboardService/Dockerfile"
  # FR-17, UC-10 (#908/#957): 知識グラフ。Dockerfile は #929 で入ったが compose / MAPPING への登録が
  # 漏れており、イメージが焼かれずデプロイにも出ていなかった。
  "microservices-platform/graph-service|src/knowledge/backend/Services/GraphService/Dockerfile"
  "microservices-platform/bff|src/platform/backend/Bff/Platform.Bff/Dockerfile"
  # 以下 3 件の SERVICE_PROJECT / SERVICE_DLL は deploy/docker-compose.yml の build args と同値でなければ
  # ならない（check-image-mapping.js の args-mismatch 検査。IADR-0068 / IADR-0070）。片側だけ動かさない。
  # Issue #570: AST がホストプロジェクトを *.Worker → *.Api へ一斉改名した（AST/IADR-0128）ため 3 件とも
  # 追随した。context / dockerfile / イメージ名は不変。
  # Issue #283, IADR-0070: AST 設定画面の ConfigurationService。単一 Dockerfile＋build args＋ユニットルート context。
  "microservices-platform/configuration-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/ConfigurationService/src/ConfigurationService.Api/ConfigurationService.Api.csproj,SERVICE_DLL=ConfigurationService.Api.dll"
  # Issue #287, IADR-0071: AST リスク設定/統制状態の RiskManagementService。同型（単一 Dockerfile＋build args＋context）。
  "microservices-platform/risk-management-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/RiskManagementService/src/RiskManagementService.Api/RiskManagementService.Api.csproj,SERVICE_DLL=RiskManagementService.Api.dll"
  # Issue #288, IADR-0072: AST 監視銘柄（watchlist）の MarketMonitorService。同型（単一 Dockerfile＋build args＋context）。
  "microservices-platform/market-monitor-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/Services/MarketMonitorService/src/MarketMonitorService.Api/MarketMonitorService.Api.csproj,SERVICE_DLL=MarketMonitorService.Api.dll"
  # Issue #313, IADR-0078: SPA(frontend) を k8s chart 配信（templates/frontend.yaml）へ移行。compose の
  # frontend build（context ルート・args 無し）と整合。従来は check-image-mapping.js の COMPOSE_ONLY で除外。
  "microservices-platform/frontend|src/platform/frontend/Dockerfile"
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
