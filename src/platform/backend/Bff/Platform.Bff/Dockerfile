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
# SC-22, IADR-0433 決定 3, IADR-0453 決定 9 (#1411): 画面から投入できる項目の集合（allowlist）。
# csproj は `../../../../../deploy/bootstrap/` を Content としてリンクする。このイメージでは `src/` を
# 剥がして /repo 直下へ置いているので、同じ相対位置は /deploy/bootstrap/ になる。**無いと publish が
# 同梱できず、BFF は起動時に落ちる**（fail-closed）。
COPY deploy/bootstrap/sc22-secret-items.json /deploy/bootstrap/sc22-secret-items.json
RUN dotnet restore platform/backend/Bff/Platform.Bff/Platform.Bff.csproj
RUN dotnet publish platform/backend/Bff/Platform.Bff/Platform.Bff.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Platform.Bff.dll"]
