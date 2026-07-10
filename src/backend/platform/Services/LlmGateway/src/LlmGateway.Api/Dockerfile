FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/backend/ .
RUN dotnet restore platform/Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj
RUN dotnet build Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj \
    -c Release -o /app/build

FROM build AS publish
RUN dotnet publish platform/Services/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LlmGateway.Api.dll"]
