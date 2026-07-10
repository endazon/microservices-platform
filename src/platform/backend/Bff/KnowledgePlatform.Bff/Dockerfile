FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/platform/backend/ platform/backend/
RUN dotnet restore platform/backend/Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj
RUN dotnet publish platform/backend/Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KnowledgePlatform.Bff.dll"]
