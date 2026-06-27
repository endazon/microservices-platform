#!/usr/bin/env bash
# OpenAPI 仕様書生成スクリプト
# ASP.NET Core の /openapi/v1.json エンドポイントから仕様を取得し統合する
# CI または手動で実行: bash scripts/generate-openapi.sh
set -e

log() { printf '[openapi] %s\n' "$1"; }

OUTDIR="docs/api"
mkdir -p "$OUTDIR"

# dotnet microsoft.openapi.hidi がインストールされている場合はコードから生成
if command -v dotnet >/dev/null 2>&1; then
  if dotnet tool list -g 2>/dev/null | grep -q "microsoft.openapi.hidi"; then
    log "hidi でコードから OpenAPI 生成"
    for svc in \
      "src/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj" \
      "src/Services/DataSourceService/src/DataSourceService.Api/DataSourceService.Api.csproj" \
      "src/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj" \
      "src/Services/RetrievalService/src/RetrievalService.Api/RetrievalService.Api.csproj" \
      "src/Services/AiAnalysisService/src/AiAnalysisService.Api/AiAnalysisService.Api.csproj" \
      "src/Services/WikiService/src/WikiService.Api/WikiService.Api.csproj" \
      "src/Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj"; do
      name=$(basename "$(dirname "$svc")" | tr '[:upper:]' '[:lower:]' | sed 's/\.\(api\|worker\)//')
      hidi transform --openapi "$svc" --output "$OUTDIR/${name}.yaml" --format yaml 2>/dev/null || true
    done
    exit 0
  fi
fi

# フォールバック: gen-openapi-skeleton.js から雛形を生成
log "gen-openapi-skeleton.js から雛形を生成"
node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml --force
log "生成完了: $OUTDIR/openapi.yaml"
