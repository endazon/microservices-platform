FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/backend/ .
RUN dotnet restore knowledge/Services/RetrievalService/src/RetrievalService.Api/RetrievalService.Api.csproj
RUN dotnet publish knowledge/Services/RetrievalService/src/RetrievalService.Api/RetrievalService.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RetrievalService.Api.dll"]
