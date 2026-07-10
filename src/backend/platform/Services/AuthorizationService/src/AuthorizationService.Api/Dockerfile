FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/backend/ .
RUN dotnet restore platform/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj
RUN dotnet publish platform/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AuthorizationService.Api.dll"]
