FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/platform/backend/ platform/backend/
COPY src/knowledge/backend/ knowledge/backend/
RUN dotnet restore knowledge/backend/Services/ConversionService/ConversionService.csproj
RUN dotnet publish knowledge/backend/Services/ConversionService/ConversionService.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# FR-12, UC-06, ADR-0012, IADR-0320 (#1097): 🔴 **pandoc は本サービスの実行時依存である。**
# `PandocConversionService` は `Process.Start("pandoc")` で外部プロセスとして起動する。
# これが無いと本文変換は例外にならず **プレースホルダ本文（図0件）を返して「成功」する** ——
# 変換ジョブ画面には成功として並び、「変換した」と「変換したふりをした」が区別できなくなる。
# **消すときは `ConversionOptions.AllowDegradedBodyConversion` の既定（fail-closed）と
# readiness の pandoc チェックを同時に読むこと。** 消すと readiness が落ちて Pod は Ready にならない。
#
# 取得元は**ベースイメージ（Ubuntu 24.04 noble）に設定済みの APT ミラー**である。
# 外部 CDN・GitHub リリース資産・任意 URL からは取らない（配布元を 1 つに保つ）。
# 版は noble の pandoc 3.1.3。イメージは約 +190MB（実測は PR 本文）。
#
# FR-12, ADR-0070 決定 2, IADR-0356 (#1192): 🔴 **poppler-utils（pdftotext）も本サービスの実行時依存である。**
# pandoc は PDF を入力に取れないため、PDF の本文は `PdfTextLayerConverter` が `pdftotext` を
# 外部プロセスとして起動して取り出す。これが無いと PDF の変換は既定で失敗する（fail-closed。
# `PdfToTextHealthCheck` が readiness で見るので Pod は Ready にならない）。
# 版は noble の poppler-utils 24.02.0。イメージの増分は PR 本文に実測を残す。
RUN apt-get update \
    && apt-get install -y --no-install-recommends pandoc poppler-utils \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConversionService.dll"]
