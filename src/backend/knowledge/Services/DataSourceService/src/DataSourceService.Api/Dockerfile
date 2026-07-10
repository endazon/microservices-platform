FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/backend/ .
RUN dotnet restore knowledge/Services/DataSourceService/src/DataSourceService.Api/DataSourceService.Api.csproj
RUN dotnet publish knowledge/Services/DataSourceService/src/DataSourceService.Api/DataSourceService.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DataSourceService.Api.dll"]
