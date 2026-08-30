FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/platform/backend/ platform/backend/
COPY src/knowledge/backend/ knowledge/backend/
RUN dotnet restore knowledge/backend/Services/ConversionService/ConversionService.csproj
RUN dotnet publish knowledge/backend/Services/ConversionService/ConversionService.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConversionService.dll"]
