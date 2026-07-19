FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/platform/backend/ platform/backend/
# FR-14, IADR-0063: BFF は合成点で knowledge ユニットの BFF エンドポイント
# （knowledge/backend/Bff/Knowledge.Bff.Endpoints）を参照する（例外3）。ビルドに実体が要るため同梱する。
COPY src/knowledge/backend/ knowledge/backend/
# FR-14, IADR-0063, IADR-0073, #286: BFF は合成点で AST（submodule）ユニットの BFF エンドポイント
# （ai-stock-trading/backend/Bff/AiStockTrading.Bff.Endpoints）も例外3 で参照する。ビルドに実体が要るため
# 当該 Bff プロジェクトを同梱する（DTO 非結合の pass-through。上位 Directory.Build.props を継承する）。
COPY src/ai-stock-trading/backend/Bff/ ai-stock-trading/backend/Bff/
RUN dotnet restore platform/backend/Bff/Platform.Bff/Platform.Bff.csproj
RUN dotnet publish platform/backend/Bff/Platform.Bff/Platform.Bff.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Platform.Bff.dll"]
