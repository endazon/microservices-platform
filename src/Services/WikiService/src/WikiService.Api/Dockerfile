FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Services/WikiService/src/WikiService.Api/WikiService.Api.csproj
RUN dotnet publish Services/WikiService/src/WikiService.Api/WikiService.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WikiService.Api.dll"]
