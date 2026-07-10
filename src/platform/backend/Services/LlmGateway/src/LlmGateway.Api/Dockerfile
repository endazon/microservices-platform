FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/platform/backend/ platform/backend/
RUN dotnet restore platform/backend/Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj
RUN dotnet build Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj \
    -c Release -o /app/build

FROM build AS publish
RUN dotnet publish platform/backend/Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LlmGateway.Api.dll"]
