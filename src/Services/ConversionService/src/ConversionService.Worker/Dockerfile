FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj
RUN dotnet publish Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConversionService.Worker.dll"]
