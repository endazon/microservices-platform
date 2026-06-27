FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Services/IngestionService/src/IngestionService.Worker/IngestionService.Worker.csproj
RUN dotnet publish Services/IngestionService/src/IngestionService.Worker/IngestionService.Worker.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IngestionService.Worker.dll"]
