# P0 Foundation Phase Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the complete .NET 8 microservices skeleton (10 services + 2 shared libraries) with local docker-compose dev environment and CI/CD, satisfying the P0 "foundation ready" milestone.

**Architecture:** Monorepo under `src/`. All services share `KnowledgePlatform.Shared.Contracts` (events/DTOs) and `KnowledgePlatform.Shared.Infrastructure` (OTEL/auth/health). REST API services use ASP.NET Core 8 Minimal APIs. Worker services use `IHostedService` + MassTransit consumers.

**Tech Stack:** .NET 8 / C# 12 / ASP.NET Core 8 Minimal APIs / MassTransit 8 / RabbitMQ / PostgreSQL / Redis / Qdrant / Keycloak / OpenTelemetry / Serilog / xUnit / FluentAssertions / Microsoft.AspNetCore.Mvc.Testing

## Global Constraints

- Runtime: .NET 8 LTS, C# 12 (`<LangVersion>12</LangVersion>`)
- All REST services: Minimal APIs (`MapGet`/`MapPost`), no MVC controllers
- Test framework: xUnit + FluentAssertions
- OTel service name: `knowledge-platform.{kebab-case-service-name}`
- Health endpoints on ALL REST services: `GET /health/live` → 200, `GET /health/ready` → 200
- All Dockerfiles: multi-stage, build from **repo root** (not service directory)
- OTel Collector: `http://otel-collector:4317` (OTLP gRPC)
- RabbitMQ: `amqp://guest:guest@rabbitmq:5672`
- PostgreSQL: `Host=postgres;Port=5432;Username=kp;Password=kp`
- Redis: `redis:6379`
- Qdrant: `http://qdrant:6333`
- Keycloak issuer: `http://keycloak:8080/realms/knowledge-platform`

Port assignments:
| Service | HTTP Port |
|---|---|
| BFF | 5000 |
| DocumentService | 5001 |
| DataSourceService | 5002 |
| RetrievalService | 5003 |
| AiAnalysisService | 5004 |
| AuthorizationService | 5005 |
| WikiService | 5006 |
| LlmGateway | 5007 |
| ConversionService | Worker (no HTTP) |
| IngestionService | Worker (no HTTP) |

---

### Task 1: Solution scaffold + central build props

**Files:**
- Create: `src/global.json`
- Create: `src/Directory.Build.props`
- Create: `src/Directory.Packages.props`
- Create: `src/KnowledgePlatform.sln` (empty, projects added per task)

**Interfaces:**
- Produces: Central package versions consumed by every `.csproj`

- [ ] **Step 1: Create `src/global.json`**

```json
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestMinor"
  }
}
```

- [ ] **Step 2: Create `src/Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create `src/Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- ASP.NET Core -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="8.0.16" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.16" />
    <!-- OpenTelemetry -->
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.Otlp" Version="1.12.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
    <!-- Logging -->
    <PackageVersion Include="Serilog.AspNetCore" Version="8.0.3" />
    <PackageVersion Include="Serilog.Sinks.OpenTelemetry" Version="4.0.0" />
    <!-- MassTransit -->
    <PackageVersion Include="MassTransit" Version="8.4.1" />
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.4.1" />
    <!-- EF Core -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.16" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.11" />
    <!-- Health checks -->
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="8.0.16" />
    <PackageVersion Include="AspNetCore.HealthChecks.NpgSql" Version="8.0.2" />
    <PackageVersion Include="AspNetCore.HealthChecks.Rabbitmq" Version="8.0.1" />
    <PackageVersion Include="AspNetCore.HealthChecks.Redis" Version="8.0.1" />
    <PackageVersion Include="AspNetCore.HealthChecks.Uris" Version="8.0.2" />
    <!-- HTTP -->
    <PackageVersion Include="Refit.HttpClientFactory" Version="7.2.22" />
    <PackageVersion Include="Polly.Extensions.Http" Version="3.0.0" />
    <!-- Test -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="FluentAssertions" Version="6.12.2" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.16" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create empty solution**

```bash
cd src
dotnet new sln -n KnowledgePlatform
```

- [ ] **Step 5: Verify**

```bash
cd src
dotnet sln list
```
Expected: `No projects found in the solution.`

---

### Task 2: Shared.Contracts library

**Files:**
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/KnowledgePlatform.Shared.Contracts.csproj`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Events/RawDocumentFetched.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Events/DocumentNormalized.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Events/DocumentUpdated.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Events/IngestionRequested.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Events/IngestionCompleted.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/DocumentDto.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/ChunkDto.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/SearchResultDto.cs`
- Test: `src/Shared/KnowledgePlatform.Shared.Contracts.Tests/ContractsTests.cs`

**Interfaces:**
- Produces: `RawDocumentFetched`, `DocumentNormalized`, `DocumentUpdated`, `IngestionRequested`, `IngestionCompleted` event records; `DocumentDto`, `ChunkDto`, `SearchResultDto` DTOs

- [ ] **Step 1: Create `.csproj`**

```xml
<!-- src/Shared/KnowledgePlatform.Shared.Contracts/KnowledgePlatform.Shared.Contracts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MassTransit" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing test**

```xml
<!-- src/Shared/KnowledgePlatform.Shared.Contracts.Tests/KnowledgePlatform.Shared.Contracts.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <ProjectReference Include="..\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts.Tests/ContractsTests.cs
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace KnowledgePlatform.Shared.Contracts.Tests;

public class ContractsTests
{
    [Fact]
    public void RawDocumentFetched_ShouldHaveRequiredProperties()
    {
        var evt = new RawDocumentFetched(
            Guid.NewGuid(), "filesystem", "/docs/file.docx",
            "s3://bucket/raw/file.docx", DateTimeOffset.UtcNow);
        evt.SourceId.Should().NotBeEmpty();
        evt.SourceType.Should().Be("filesystem");
    }

    [Fact]
    public void DocumentDto_ShouldDefaultStatusToDraft()
    {
        var dto = new DocumentDto { Id = Guid.NewGuid(), Title = "Test" };
        dto.Status.Should().Be("draft");
    }
}
```

- [ ] **Step 3: Run test (expect failure — types not defined yet)**

```bash
cd src
dotnet test Shared/KnowledgePlatform.Shared.Contracts.Tests
```
Expected: Build error — `RawDocumentFetched` not found.

- [ ] **Step 4: Create event types**

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Events/RawDocumentFetched.cs
namespace KnowledgePlatform.Shared.Contracts.Events;

public record RawDocumentFetched(
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string StorageUri,
    DateTimeOffset FetchedAt);
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Events/DocumentNormalized.cs
namespace KnowledgePlatform.Shared.Contracts.Events;

public record DocumentNormalized(
    Guid DocumentId,
    string MarkdownStorageUri,
    DateTimeOffset NormalizedAt);
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Events/DocumentUpdated.cs
namespace KnowledgePlatform.Shared.Contracts.Events;

public record DocumentUpdated(
    Guid DocumentId,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt);
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Events/IngestionRequested.cs
namespace KnowledgePlatform.Shared.Contracts.Events;

public record IngestionRequested(
    Guid DocumentId,
    Guid JobId,
    DateTimeOffset RequestedAt);
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Events/IngestionCompleted.cs
namespace KnowledgePlatform.Shared.Contracts.Events;

public record IngestionCompleted(
    Guid DocumentId,
    Guid JobId,
    int ChunkCount,
    DateTimeOffset CompletedAt);
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/DocumentDto.cs
namespace KnowledgePlatform.Shared.Contracts.Dtos;

public class DocumentDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string? MarkdownUri { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/ChunkDto.cs
namespace KnowledgePlatform.Shared.Contracts.Dtos;

public class ChunkDto
{
    public Guid ChunkId { get; init; }
    public Guid DocumentId { get; init; }
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = string.Empty;
    public float Score { get; init; }
}
```

```csharp
// src/Shared/KnowledgePlatform.Shared.Contracts/Dtos/SearchResultDto.cs
namespace KnowledgePlatform.Shared.Contracts.Dtos;

public class SearchResultDto
{
    public string Query { get; init; } = string.Empty;
    public List<ChunkDto> Chunks { get; init; } = [];
    public string? AiAnswer { get; init; }
    public List<string> SourceUris { get; init; } = [];
}
```

- [ ] **Step 5: Add projects to solution and run tests**

```bash
cd src
dotnet sln add Shared/KnowledgePlatform.Shared.Contracts/KnowledgePlatform.Shared.Contracts.csproj
dotnet sln add Shared/KnowledgePlatform.Shared.Contracts.Tests/KnowledgePlatform.Shared.Contracts.Tests.csproj
dotnet test Shared/KnowledgePlatform.Shared.Contracts.Tests
```
Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Shared/KnowledgePlatform.Shared.Contracts src/KnowledgePlatform.sln src/global.json src/Directory.Build.props src/Directory.Packages.props
git commit -m "feat: add shared contracts library with events and DTOs"
```

---

### Task 3: Shared.Infrastructure library

**Files:**
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/KnowledgePlatform.Shared.Infrastructure.csproj`
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/ObservabilityExtensions.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/AuthExtensions.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/HealthCheckExtensions.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/CommonServiceExtensions.cs`
- Create: `src/Shared/KnowledgePlatform.Shared.Infrastructure/Middleware/CorrelationIdMiddleware.cs`

**Interfaces:**
- Produces:
  - `IServiceCollection.AddKnowledgePlatformObservability(IConfiguration config, string serviceName)`
  - `IServiceCollection.AddKnowledgePlatformAuth(IConfiguration config)`
  - `WebApplication.UseKnowledgePlatformMiddleware()`

- [ ] **Step 1: Create `.csproj`**

```xml
<!-- src/Shared/KnowledgePlatform.Shared.Infrastructure/KnowledgePlatform.Shared.Infrastructure.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
    <PackageReference Include="OpenTelemetry.Exporter.Otlp" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="Serilog.Sinks.OpenTelemetry" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create ObservabilityExtensions.cs**

```csharp
// src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/ObservabilityExtensions.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace KnowledgePlatform.Shared.Infrastructure.Extensions;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddKnowledgePlatformObservability(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = config["Otlp:Endpoint"] ?? "http://otel-collector:4317";
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: "0.1.0");

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

        return services;
    }

    public static void ConfigureKnowledgePlatformSerilog(
        this LoggerConfiguration loggerConfig,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = config["Otlp:Endpoint"] ?? "http://otel-collector:4317";
        loggerConfig
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ServiceName", serviceName)
            .WriteTo.Console()
            .WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = serviceName
                };
            });
    }
}
```

- [ ] **Step 3: Create AuthExtensions.cs**

```csharp
// src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/AuthExtensions.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgePlatform.Shared.Infrastructure.Extensions;

public static class AuthExtensions
{
    public static IServiceCollection AddKnowledgePlatformAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        var authority = config["Auth:Authority"]
            ?? "http://keycloak:8080/realms/knowledge-platform";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ValidateAudience = false;
            });

        services.AddAuthorization();
        return services;
    }
}
```

- [ ] **Step 4: Create HealthCheckExtensions.cs**

```csharp
// src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/HealthCheckExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KnowledgePlatform.Shared.Infrastructure.Extensions;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddKnowledgePlatformHealthChecks(
        this IServiceCollection services) =>
        services.AddHealthChecks();

    public static WebApplication MapKnowledgePlatformHealthChecks(
        this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("ready")
        });
        return app;
    }
}
```

- [ ] **Step 5: Create CorrelationIdMiddleware.cs**

```csharp
// src/Shared/KnowledgePlatform.Shared.Infrastructure/Middleware/CorrelationIdMiddleware.cs
using Microsoft.AspNetCore.Http;

namespace KnowledgePlatform.Shared.Infrastructure.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId))
            correlationId = Guid.NewGuid().ToString();

        context.Response.Headers[CorrelationIdHeader] = correlationId;
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId.ToString()))
        {
            await next(context);
        }
    }
}
```

- [ ] **Step 6: Create CommonServiceExtensions.cs**

```csharp
// src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/CommonServiceExtensions.cs
using Microsoft.AspNetCore.Builder;
using KnowledgePlatform.Shared.Infrastructure.Middleware;

namespace KnowledgePlatform.Shared.Infrastructure.Extensions;

public static class CommonServiceExtensions
{
    public static WebApplication UseKnowledgePlatformMiddleware(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
```

- [ ] **Step 7: Add to solution, build**

```bash
cd src
dotnet sln add Shared/KnowledgePlatform.Shared.Infrastructure/KnowledgePlatform.Shared.Infrastructure.csproj
dotnet build KnowledgePlatform.sln
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Shared/KnowledgePlatform.Shared.Infrastructure
git commit -m "feat: add shared infrastructure library (OTEL, auth, health checks)"
```

---

### Task 4: DocumentService skeleton (REST API template)

**Files:**
- Create: `src/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj`
- Create: `src/Services/DocumentService/src/DocumentService.Api/Program.cs`
- Create: `src/Services/DocumentService/src/DocumentService.Api/appsettings.json`
- Create: `src/Services/DocumentService/src/DocumentService.Api/appsettings.Development.json`
- Create: `src/Services/DocumentService/src/DocumentService.Api/Endpoints/DocumentEndpoints.cs`
- Create: `src/Services/DocumentService/src/DocumentService.Api/Dockerfile`
- Create: `src/Services/DocumentService/tests/DocumentService.Api.Tests/DocumentService.Api.Tests.csproj`
- Create: `src/Services/DocumentService/tests/DocumentService.Api.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes: `KnowledgePlatform.Shared.Infrastructure` (auth, OTEL, health)
- Produces: `GET /health/live` → 200, `GET /health/ready` → 200, `GET /documents` → 200 `[]`

- [ ] **Step 1: Write failing test first**

```xml
<!-- src/Services/DocumentService/tests/DocumentService.Api.Tests/DocumentService.Api.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <ProjectReference Include="..\..\src\DocumentService.Api\DocumentService.Api.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// src/Services/DocumentService/tests/DocumentService.Api.Tests/HealthEndpointTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocumentService.Api.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetDocuments_Returns200EmptyArray()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/documents");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Create `.csproj`**

```xml
<!-- src/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="MassTransit.RabbitMQ" />
    <PackageReference Include="AspNetCore.HealthChecks.NpgSql" />
    <PackageReference Include="AspNetCore.HealthChecks.Rabbitmq" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Program.cs**

```csharp
// src/Services/DocumentService/src/DocumentService.Api/Program.cs
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using Serilog;

const string ServiceName = "knowledge-platform.document-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

DocumentEndpoints.Map(app);

app.Run();

public partial class Program { }
```

- [ ] **Step 4: Create DocumentEndpoints.cs**

```csharp
// src/Services/DocumentService/src/DocumentService.Api/Endpoints/DocumentEndpoints.cs
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace DocumentService.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/documents").WithTags("Documents");

        // FR-06: Document CRUD skeleton — returns empty list until P1 implements persistence
        group.MapGet("/", () => Results.Ok(Array.Empty<DocumentDto>()))
            .WithName("GetDocuments")
            .Produces<DocumentDto[]>();

        group.MapGet("/{id:guid}", (Guid id) =>
            Results.NotFound(new { message = $"Document {id} not found (stub)" }))
            .WithName("GetDocument")
            .Produces<DocumentDto>()
            .Produces(404);

        group.MapPost("/", (DocumentDto dto) =>
            Results.Accepted($"/documents/{dto.Id}", dto))
            .WithName("CreateDocument")
            .Produces<DocumentDto>(201);
    }
}
```

- [ ] **Step 5: Create appsettings.json**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Otlp": {
    "Endpoint": "http://otel-collector:4317"
  },
  "Auth": {
    "Authority": "http://keycloak:8080/realms/knowledge-platform"
  },
  "RabbitMq": {
    "ConnectionString": "amqp://guest:guest@rabbitmq:5672"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp"
  }
}
```

- [ ] **Step 6: Create appsettings.Development.json**

```json
{
  "Otlp": {
    "Endpoint": "http://localhost:4317"
  },
  "Auth": {
    "Authority": "http://localhost:8080/realms/knowledge-platform"
  },
  "RabbitMq": {
    "ConnectionString": "amqp://guest:guest@localhost:5672"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=document_svc;Username=kp;Password=kp"
  }
}
```

- [ ] **Step 7: Create Dockerfile**

```dockerfile
# src/Services/DocumentService/src/DocumentService.Api/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj
RUN dotnet publish Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocumentService.Api.dll"]
```

- [ ] **Step 8: Add to solution and run tests**

```bash
cd src
dotnet sln add Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj
dotnet sln add Services/DocumentService/tests/DocumentService.Api.Tests/DocumentService.Api.Tests.csproj
dotnet test Services/DocumentService/tests/DocumentService.Api.Tests --no-build -v normal 2>&1 | head -30
```

Note: Health/ready check will fail because PostgreSQL/RabbitMQ aren't running locally. This is expected. The live check must pass. For CI, add an environment variable `ASPNETCORE_ENVIRONMENT=Testing` and override the connection strings.

Create `src/Services/DocumentService/tests/DocumentService.Api.Tests/appsettings.Testing.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=test_document_svc;Username=postgres;Password=postgres"
  },
  "RabbitMq": {
    "ConnectionString": "amqp://guest:guest@localhost:5672"
  },
  "Otlp": {
    "Endpoint": "http://localhost:4317"
  }
}
```

And a `WebApplicationFactory` override:

```csharp
// src/Services/DocumentService/tests/DocumentService.Api.Tests/TestWebApplicationFactory.cs
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DocumentService.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Port=5432;Database=test_db;Username=postgres;Password=postgres",
                ["RabbitMq:ConnectionString"] = "amqp://guest:guest@localhost:5672",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            });
        });
    }
}
```

Update `HealthEndpointTests.cs` to use `TestWebApplicationFactory`:

```csharp
// src/Services/DocumentService/tests/DocumentService.Api.Tests/HealthEndpointTests.cs
using FluentAssertions;

namespace DocumentService.Api.Tests;

public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetDocuments_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/documents");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
```

```bash
cd src
dotnet test Services/DocumentService/tests/DocumentService.Api.Tests -v normal
```
Expected: 2 tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Services/DocumentService
git commit -m "feat(FR-06): add DocumentService skeleton with health endpoints"
```

---

### Task 5: ConversionService Worker skeleton

**Files:**
- Create: `src/Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj`
- Create: `src/Services/ConversionService/src/ConversionService.Worker/Program.cs`
- Create: `src/Services/ConversionService/src/ConversionService.Worker/Consumers/RawDocumentFetchedConsumer.cs`
- Create: `src/Services/ConversionService/src/ConversionService.Worker/appsettings.json`
- Create: `src/Services/ConversionService/src/ConversionService.Worker/Dockerfile`
- Create: `src/Services/ConversionService/tests/ConversionService.Worker.Tests/ConversionService.Worker.Tests.csproj`
- Create: `src/Services/ConversionService/tests/ConversionService.Worker.Tests/RawDocumentFetchedConsumerTests.cs`

**Interfaces:**
- Consumes: `RawDocumentFetched` event via MassTransit
- Produces: (P1) will publish `DocumentNormalized` event

- [ ] **Step 1: Write failing consumer test**

```xml
<!-- src/Services/ConversionService/tests/ConversionService.Worker.Tests/ConversionService.Worker.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="MassTransit.RabbitMQ" />
    <ProjectReference Include="..\..\src\ConversionService.Worker\ConversionService.Worker.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// src/Services/ConversionService/tests/ConversionService.Worker.Tests/RawDocumentFetchedConsumerTests.cs
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using ConversionService.Worker.Consumers;

namespace ConversionService.Worker.Tests;

public class RawDocumentFetchedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeRawDocumentFetchedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(cfg =>
                cfg.AddConsumer<RawDocumentFetchedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var evt = new RawDocumentFetched(
            Guid.NewGuid(), "filesystem", "/docs/test.docx",
            "s3://bucket/raw/test.docx", DateTimeOffset.UtcNow);

        await harness.Bus.Publish(evt);

        (await harness.Consumed.Any<RawDocumentFetched>())
            .Should().BeTrue(because: "consumer should process the event");

        await harness.Stop();
    }
}
```

- [ ] **Step 2: Create `.csproj`**

```xml
<!-- src/Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <ItemGroup>
    <PackageReference Include="MassTransit.RabbitMQ" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Exporter.Otlp" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Consumer**

```csharp
// src/Services/ConversionService/src/ConversionService.Worker/Consumers/RawDocumentFetchedConsumer.cs
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ConversionService.Worker.Consumers;

// FR-12, UC-06: 原本を正規化する（pandoc + LLM）— P0 はスタブ、P1 で実装
public class RawDocumentFetchedConsumer(ILogger<RawDocumentFetchedConsumer> logger)
    : IConsumer<RawDocumentFetched>
{
    public Task Consume(ConsumeContext<RawDocumentFetched> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Received RawDocumentFetched: SourceId={SourceId} Path={Path}",
            msg.SourceId, msg.OriginalPath);
        // P1: call pandoc, call LLM gateway, publish DocumentNormalized
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Create Program.cs**

```csharp
// src/Services/ConversionService/src/ConversionService.Worker/Program.cs
using ConversionService.Worker.Consumers;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "knowledge-platform.conversion-service";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(builder.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RawDocumentFetchedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
```

- [ ] **Step 5: Create appsettings.json**

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Otlp": { "Endpoint": "http://otel-collector:4317" },
  "RabbitMq": { "ConnectionString": "amqp://guest:guest@rabbitmq:5672" }
}
```

- [ ] **Step 6: Create Dockerfile**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj
RUN dotnet publish Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConversionService.Worker.dll"]
```

- [ ] **Step 7: Add to solution and run tests**

```bash
cd src
dotnet sln add Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj
dotnet sln add Services/ConversionService/tests/ConversionService.Worker.Tests/ConversionService.Worker.Tests.csproj
dotnet test Services/ConversionService/tests/ConversionService.Worker.Tests -v normal
```
Expected: 1 test passes.

- [ ] **Step 8: Commit**

```bash
git add src/Services/ConversionService
git commit -m "feat(FR-12): add ConversionService Worker skeleton with MassTransit consumer"
```

---

### Task 6: Remaining REST API services (DataSource, Retrieval, AiAnalysis, Authorization, Wiki)

Each service follows the exact same pattern as Task 4 (DocumentService). Create them by substituting the values below.

| Service | Port | DB name | Health checks | Key endpoints |
|---|---|---|---|---|
| DataSourceService | 5002 | datasource_svc | NpgSql + RabbitMQ | `GET /datasources`, `POST /datasources`, `POST /datasources/{id}/sync` |
| RetrievalService | 5003 | retrieval_svc | NpgSql | `GET /search?q=&limit=10` |
| AiAnalysisService | 5004 | aianalysis_svc | NpgSql + RabbitMQ | `POST /analysis/ask`, `GET /analysis/sessions/{id}` |
| AuthorizationService | 5005 | authz_svc | NpgSql | `POST /authz/check`, `GET /authz/policies` |
| WikiService | 5006 | wiki_svc | NpgSql + external URI (Wiki.js) | `GET /wiki/pages`, `POST /wiki/sync/{documentId}` |

For each service:

- [ ] Create `{ServiceName}.Api.csproj` (same deps as DocumentService.Api.csproj)
- [ ] Create `Program.cs` (same pattern, change `ServiceName` constant and health check DB name)
- [ ] Create `Endpoints/{ServiceName}Endpoints.cs` with stub endpoints
- [ ] Create `appsettings.json` (same pattern, change DB name)
- [ ] Create `appsettings.Development.json`
- [ ] Create `Dockerfile` (change project path)
- [ ] Create `tests/{ServiceName}.Api.Tests/{ServiceName}.Api.Tests.csproj`
- [ ] Create `tests/{ServiceName}.Api.Tests/TestWebApplicationFactory.cs`
- [ ] Create `tests/{ServiceName}.Api.Tests/HealthEndpointTests.cs`
- [ ] Add all projects to solution: `dotnet sln add ...`
- [ ] Run tests: `dotnet test Services/{ServiceName}/tests/...`

**DataSourceService key stub:**
```csharp
// DataSourceEndpoints.cs
group.MapGet("/", () => Results.Ok(Array.Empty<object>())).WithName("GetDataSources");
group.MapPost("/", (object dto) => Results.Accepted("/datasources/new", dto)).WithName("CreateDataSource");
group.MapPost("/{id:guid}/sync", (Guid id) => Results.Accepted($"/datasources/{id}/sync/job-1", new { jobId = "job-1", status = "queued" })).WithName("SyncDataSource");
```

**RetrievalService key stub:**
```csharp
// SearchEndpoints.cs — FR-03, UC-01
group.MapGet("/", (string? q, int limit = 10) =>
    Results.Ok(new KnowledgePlatform.Shared.Contracts.Dtos.SearchResultDto { Query = q ?? "" }))
    .WithName("Search");
```

**AiAnalysisService key stub:**
```csharp
// AnalysisEndpoints.cs — FR-04, FR-07, UC-01, UC-02
group.MapPost("/ask", (object request) =>
    Results.Ok(new { answer = "stub answer", sources = Array.Empty<string>() }))
    .WithName("Ask");
group.MapGet("/sessions/{id:guid}", (Guid id) =>
    Results.NotFound(new { message = $"Session {id} not found (stub)" }))
    .WithName("GetSession");
```

**AuthorizationService key stub:**
```csharp
// AuthzEndpoints.cs — FR-05, FR-09, UC-05, ADR-0004
group.MapPost("/check", (object request) =>
    Results.Ok(new { allowed = true, reason = "stub — P2 implements ABAC" }))
    .WithName("CheckAccess");
group.MapGet("/policies", () =>
    Results.Ok(Array.Empty<object>()))
    .WithName("GetPolicies");
```

**WikiService key stub:**
```csharp
// WikiEndpoints.cs — FR-13, UC-07, ADR-0011
group.MapGet("/pages", () => Results.Ok(Array.Empty<object>())).WithName("GetWikiPages");
group.MapPost("/sync/{documentId:guid}", (Guid documentId) =>
    Results.Accepted($"/wiki/sync/{documentId}", new { status = "queued (stub)" }))
    .WithName("SyncDocumentToWiki");
```

After all 5 services are created:

- [ ] **Batch run all tests**

```bash
cd src
dotnet test KnowledgePlatform.sln -v minimal
```
Expected: All tests pass.

- [ ] **Commit**

```bash
git add src/Services/DataSourceService src/Services/RetrievalService src/Services/AiAnalysisService src/Services/AuthorizationService src/Services/WikiService
git commit -m "feat: add DataSource, Retrieval, AiAnalysis, Authorization, Wiki service skeletons"
```

---

### Task 7: IngestionService Worker skeleton

**Files:** Same pattern as ConversionService (Task 5), for IngestionService.

- Service name: `knowledge-platform.ingestion-service`
- Consumes: `DocumentUpdated` event (from DocumentService via MassTransit)
- Namespace: `IngestionService.Worker.Consumers`
- Consumer class: `DocumentUpdatedConsumer`

- [ ] **Create `.csproj`** (same as ConversionService.Worker.csproj)

```xml
<!-- src/Services/IngestionService/src/IngestionService.Worker/IngestionService.Worker.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <ItemGroup>
    <PackageReference Include="MassTransit.RabbitMQ" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Exporter.Otlp" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Create consumer**

```csharp
// src/Services/IngestionService/src/IngestionService.Worker/Consumers/DocumentUpdatedConsumer.cs
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Consumers;

// FR-02, UC-04: パース→チャンク→埋め込み→索引登録 — P0 stub, P1 で実装
public class DocumentUpdatedConsumer(ILogger<DocumentUpdatedConsumer> logger)
    : IConsumer<DocumentUpdated>
{
    public Task Consume(ConsumeContext<DocumentUpdated> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Received DocumentUpdated: DocumentId={DocumentId} Title={Title}",
            msg.DocumentId, msg.Title);
        // P1: chunk text, call LlmGateway for embeddings, write to Qdrant
        return Task.CompletedTask;
    }
}
```

- [ ] **Create Program.cs** (same pattern as ConversionService, change consumer + service name)

```csharp
// src/Services/IngestionService/src/IngestionService.Worker/Program.cs
using IngestionService.Worker.Consumers;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "knowledge-platform.ingestion-service";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(builder.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DocumentUpdatedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
```

- [ ] **Create test**

```csharp
// src/Services/IngestionService/tests/IngestionService.Worker.Tests/DocumentUpdatedConsumerTests.cs
using FluentAssertions;
using IngestionService.Worker.Consumers;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionService.Worker.Tests;

public class DocumentUpdatedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeDocumentUpdatedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentUpdatedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new DocumentUpdated(
            Guid.NewGuid(), "Test Doc", "active", DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
        await harness.Stop();
    }
}
```

- [ ] **Create `appsettings.json`, `Dockerfile`, test `.csproj`** (same pattern as ConversionService)
- [ ] **Add to solution and run tests**

```bash
cd src
dotnet sln add Services/IngestionService/src/IngestionService.Worker/IngestionService.Worker.csproj
dotnet sln add Services/IngestionService/tests/IngestionService.Worker.Tests/IngestionService.Worker.Tests.csproj
dotnet test Services/IngestionService/tests/IngestionService.Worker.Tests -v normal
```
Expected: 1 test passes.

- [ ] **Commit**

```bash
git add src/Services/IngestionService
git commit -m "feat(FR-02): add IngestionService Worker skeleton with MassTransit consumer"
```

---

### Task 8: BFF skeleton

**Files:**
- Create: `src/Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj`
- Create: `src/Bff/KnowledgePlatform.Bff/Program.cs`
- Create: `src/Bff/KnowledgePlatform.Bff/Endpoints/SearchBffEndpoints.cs`
- Create: `src/Bff/KnowledgePlatform.Bff/Endpoints/AnalysisBffEndpoints.cs`
- Create: `src/Bff/KnowledgePlatform.Bff/appsettings.json`
- Create: `src/Bff/KnowledgePlatform.Bff/Dockerfile`
- Create: `src/Bff/KnowledgePlatform.Bff.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes: downstream services via `IHttpClientFactory` (stubs in P0)
- Produces: `GET /health/live`, `GET /health/ready`, `POST /bff/search`, `POST /bff/ask`

- [ ] **Create `.csproj`**

```xml
<!-- src/Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Refit.HttpClientFactory" />
    <PackageReference Include="AspNetCore.HealthChecks.Uris" />
    <PackageReference Include="AspNetCore.HealthChecks.Redis" />
    <ProjectReference Include="..\..\Shared\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
    <ProjectReference Include="..\..\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Create Program.cs**

```csharp
// src/Bff/KnowledgePlatform.Bff/Program.cs
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using Serilog;

const string ServiceName = "knowledge-platform.bff";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);

builder.Services.AddKnowledgePlatformHealthChecks()
    .AddRedis(
        builder.Configuration["Redis:ConnectionString"] ?? "redis:6379",
        tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:AiAnalysisService"] ?? "http://aianalysis-service:5004") + "/health/live"),
        "aianalysis-service", tags: ["ready"]);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

SearchBffEndpoints.Map(app);
AnalysisBffEndpoints.Map(app);

app.Run();

public partial class Program { }
```

- [ ] **Create SearchBffEndpoints.cs**

```csharp
// src/Bff/KnowledgePlatform.Bff/Endpoints/SearchBffEndpoints.cs
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace KnowledgePlatform.Bff.Endpoints;

public static class SearchBffEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/bff/search").WithTags("Search BFF");

        // FR-03, FR-04, UC-01: 横断検索 + AI回答 (stub — P1 で RetrievalService + AiAnalysisService を呼ぶ)
        group.MapPost("/", (SearchRequest request) =>
            Results.Ok(new SearchResultDto
            {
                Query = request.Query,
                AiAnswer = "stub answer (P1 will call RetrievalService + AiAnalysisService)"
            }))
            .WithName("BffSearch")
            .Produces<SearchResultDto>();
    }
}

public record SearchRequest(string Query, int Limit = 10);
```

- [ ] **Create AnalysisBffEndpoints.cs**

```csharp
// src/Bff/KnowledgePlatform.Bff/Endpoints/AnalysisBffEndpoints.cs
namespace KnowledgePlatform.Bff.Endpoints;

public static class AnalysisBffEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/bff/analysis").WithTags("Analysis BFF");

        // FR-07, UC-02: AI 分析依頼 (stub)
        group.MapPost("/ask", (AnalysisRequest request) =>
            Results.Accepted("/bff/analysis/sessions/stub-session-id",
                new { sessionId = "stub-session-id", status = "processing" }))
            .WithName("BffAnalysisAsk");
    }
}

public record AnalysisRequest(string Question, string? Scope = null);
```

- [ ] **Create appsettings.json**

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Otlp": { "Endpoint": "http://otel-collector:4317" },
  "Auth": { "Authority": "http://keycloak:8080/realms/knowledge-platform" },
  "Redis": { "ConnectionString": "redis:6379" },
  "Services": {
    "RetrievalService": "http://retrieval-service:5003",
    "AiAnalysisService": "http://aianalysis-service:5004",
    "DocumentService": "http://document-service:5001",
    "AuthorizationService": "http://authorization-service:5005",
    "WikiService": "http://wiki-service:5006"
  }
}
```

- [ ] **Create Dockerfile**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/ .
RUN dotnet restore Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj
RUN dotnet publish Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KnowledgePlatform.Bff.dll"]
```

- [ ] **Create tests** (same pattern as DocumentService.Api.Tests)

```xml
<!-- src/Bff/KnowledgePlatform.Bff.Tests/KnowledgePlatform.Bff.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <ProjectReference Include="..\KnowledgePlatform.Bff\KnowledgePlatform.Bff.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// src/Bff/KnowledgePlatform.Bff.Tests/HealthEndpointTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgePlatform.Bff.Tests;

public class BffTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "localhost:6379",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:AiAnalysisService"] = "http://localhost:5004"
            }));
    }
}

public class HealthEndpointTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostBffSearch_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/bff/search", new { Query = "test" });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
```

- [ ] **Add to solution and run tests**

```bash
cd src
dotnet sln add Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj
dotnet sln add Bff/KnowledgePlatform.Bff.Tests/KnowledgePlatform.Bff.Tests.csproj
dotnet test Bff/KnowledgePlatform.Bff.Tests -v normal
```
Expected: 2 tests pass.

- [ ] **Commit**

```bash
git add src/Bff
git commit -m "feat: add BFF skeleton with search and analysis aggregation stubs"
```

---

### Task 9: LlmGateway skeleton

**Files:**
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Providers/ILlmProvider.cs`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Providers/ClaudeProvider.cs`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Endpoints/CompletionEndpoints.cs`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Endpoints/EmbeddingEndpoints.cs`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Program.cs`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/appsettings.json`
- Create: `src/Gateway/LlmGateway/src/LlmGateway.Api/Dockerfile`

**Interfaces:**
- Produces: `POST /completions` → `{ text, model, tokens }`, `POST /embeddings` → `{ vectors }`
- ADR-0010: abstracted LLM provider interface

- [ ] **Create `.csproj`**

```xml
<!-- src/Gateway/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Anthropic.SDK" Version="3.3.2" />
    <ProjectReference Include="..\..\..\..\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

Note: Add `Anthropic.SDK` to `Directory.Packages.props`:
```xml
<PackageVersion Include="Anthropic.SDK" Version="3.3.2" />
```

- [ ] **Create ILlmProvider.cs** (ADR-0010: abstraction)

```csharp
// src/Gateway/LlmGateway/src/LlmGateway.Api/Providers/ILlmProvider.cs
namespace LlmGateway.Api.Providers;

public interface ILlmProvider
{
    string Name { get; }
    Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

public record CompletionRequest(string Prompt, string? SystemPrompt, string? Model, int MaxTokens = 1024);
public record CompletionResult(string Text, string Model, int InputTokens, int OutputTokens);
public record EmbeddingRequest(string Text, string? Model);
public record EmbeddingResult(float[] Vector, string Model);
```

- [ ] **Create ClaudeProvider.cs**

```csharp
// src/Gateway/LlmGateway/src/LlmGateway.Api/Providers/ClaudeProvider.cs
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Configuration;

namespace LlmGateway.Api.Providers;

// ADR-0010: Claude SDK integration — default provider
public class ClaudeProvider(IConfiguration config, ILogger<ClaudeProvider> logger)
    : ILlmProvider
{
    private readonly string _defaultModel =
        config["Llm:Claude:Model"] ?? "claude-sonnet-4-6";
    private readonly string _apiKey =
        config["Llm:Claude:ApiKey"] ?? string.Empty;

    public string Name => "claude";

    public async Task<CompletionResult> CompleteAsync(
        CompletionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            logger.LogWarning("Claude API key not configured — returning stub response");
            return new CompletionResult("(stub — configure Llm:Claude:ApiKey)", _defaultModel, 0, 0);
        }

        var client = new AnthropicClient(_apiKey);
        var model = request.Model ?? _defaultModel;

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = [new TextContent { Text = request.Prompt }] }
        };

        var response = await client.Messages.GetClaudeMessageAsync(
            new MessageParameters
            {
                Model = model,
                MaxTokens = request.MaxTokens,
                System = request.SystemPrompt is not null
                    ? [new SystemMessage { Text = request.SystemPrompt }]
                    : null,
                Messages = messages
            }, ct);

        var text = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
        return new CompletionResult(text, model,
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0);
    }

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        // Claude does not provide embeddings natively; stub returns zero vector
        logger.LogWarning("Embedding via Claude not supported — returning zero vector stub");
        return Task.FromResult(new EmbeddingResult(new float[1536], "stub-embedding"));
    }
}
```

- [ ] **Create CompletionEndpoints.cs**

```csharp
// src/Gateway/LlmGateway/src/LlmGateway.Api/Endpoints/CompletionEndpoints.cs
using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

public static class CompletionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/completions").WithTags("Completions");

        // FR-11, ADR-0010: LLM provider abstraction
        group.MapPost("/", async (CompletionRequest request, ILlmProvider provider) =>
        {
            var result = await provider.CompleteAsync(request);
            return Results.Ok(result);
        })
        .WithName("Complete")
        .Produces<CompletionResult>();
    }
}
```

- [ ] **Create EmbeddingEndpoints.cs**

```csharp
// src/Gateway/LlmGateway/src/LlmGateway.Api/Endpoints/EmbeddingEndpoints.cs
using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

public static class EmbeddingEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/embeddings").WithTags("Embeddings");

        // FR-02: 埋め込み生成 — ADR-0013 embedding model selection
        group.MapPost("/", async (EmbeddingRequest request, ILlmProvider provider) =>
        {
            var result = await provider.EmbedAsync(request);
            return Results.Ok(result);
        })
        .WithName("Embed")
        .Produces<EmbeddingResult>();
    }
}
```

- [ ] **Create Program.cs**

```csharp
// src/Gateway/LlmGateway/src/LlmGateway.Api/Program.cs
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using LlmGateway.Api.Endpoints;
using LlmGateway.Api.Providers;
using Serilog;

const string ServiceName = "knowledge-platform.llm-gateway";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks();
builder.Services.AddOpenApi();

// ADR-0010: Register default LLM provider (Claude). Swap for self-hosted in config.
builder.Services.AddSingleton<ILlmProvider, ClaudeProvider>();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

CompletionEndpoints.Map(app);
EmbeddingEndpoints.Map(app);

app.Run();

public partial class Program { }
```

- [ ] **Create appsettings.json**

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "AllowedHosts": "*",
  "Otlp": { "Endpoint": "http://otel-collector:4317" },
  "Auth": { "Authority": "http://keycloak:8080/realms/knowledge-platform" },
  "Llm": {
    "Claude": {
      "Model": "claude-sonnet-4-6",
      "ApiKey": ""
    }
  }
}
```

- [ ] **Add test + Dockerfile** (same pattern as DocumentService, test: health live 200, POST /completions returns 200 with stub)
- [ ] **Add to solution and run tests**

```bash
cd src
dotnet sln add Gateway/LlmGateway/src/LlmGateway.Api/LlmGateway.Api.csproj
dotnet test KnowledgePlatform.sln -v minimal
```
Expected: All tests pass.

- [ ] **Commit**

```bash
git add src/Gateway
git commit -m "feat(FR-11,ADR-0010): add LlmGateway skeleton with Claude provider abstraction"
```

---

### Task 10: docker-compose.yml

**Files:**
- Create: `deploy/docker-compose.yml`
- Create: `deploy/docker-compose.override.yml`
- Create: `deploy/otel-collector-config.yaml`
- Create: `deploy/prometheus.yml`

- [ ] **Create `deploy/docker-compose.yml`**

```yaml
# deploy/docker-compose.yml
# ADR-0003 (RabbitMQ), ADR-0004 (Keycloak), ADR-0006 (OTEL/Prom/Loki/Tempo), ADR-0008 (k3s → local dev uses compose), ADR-0009 (Qdrant)
version: "3.9"

services:
  # ──────────────────────────────────
  # Infrastructure
  # ──────────────────────────────────
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: kp
      POSTGRES_PASSWORD: kp
      POSTGRES_DB: kp
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./init-db.sql:/docker-entrypoint-initdb.d/init-db.sql:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U kp"]
      interval: 10s
      timeout: 5s
      retries: 5

  rabbitmq:
    image: rabbitmq:3-management-alpine
    ports:
      - "5672:5672"
      - "15672:15672"  # Management UI
    environment:
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest
    volumes:
      - rabbitmq_data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "check_port_connectivity"]
      interval: 10s
      timeout: 5s
      retries: 5

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    volumes:
      - redis_data:/data
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"
      - "6334:6334"
    volumes:
      - qdrant_data:/qdrant/storage
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:6333/healthz || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5

  keycloak:
    image: quay.io/keycloak/keycloak:24.0
    command: start-dev
    environment:
      KEYCLOAK_ADMIN: admin
      KEYCLOAK_ADMIN_PASSWORD: admin
      KC_DB: dev-mem
    ports:
      - "8080:8080"
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:8080/health/ready || exit 1"]
      interval: 20s
      timeout: 10s
      retries: 10
      start_period: 30s

  # ──────────────────────────────────
  # Observability (ADR-0006)
  # ──────────────────────────────────
  otel-collector:
    image: otel/opentelemetry-collector-contrib:0.115.1
    command: ["--config=/etc/otel-collector-config.yaml"]
    volumes:
      - ./otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
    ports:
      - "4317:4317"   # OTLP gRPC
      - "4318:4318"   # OTLP HTTP
    depends_on:
      - prometheus
      - loki
      - tempo

  prometheus:
    image: prom/prometheus:v2.55.1
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--storage.tsdb.retention.time=7d"
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus_data:/prometheus

  loki:
    image: grafana/loki:3.3.2
    ports:
      - "3100:3100"
    volumes:
      - loki_data:/loki

  tempo:
    image: grafana/tempo:2.6.1
    command: ["-config.file=/etc/tempo.yaml"]
    volumes:
      - ./tempo.yaml:/etc/tempo.yaml:ro
      - tempo_data:/tmp/tempo
    ports:
      - "3200:3200"

  grafana:
    image: grafana/grafana:11.4.0
    ports:
      - "3000:3000"
    environment:
      GF_AUTH_ANONYMOUS_ENABLED: "true"
      GF_AUTH_ANONYMOUS_ORG_ROLE: "Admin"
    volumes:
      - grafana_data:/var/lib/grafana

  # ──────────────────────────────────
  # Application Services
  # ──────────────────────────────────
  document-service:
    build:
      context: ..
      dockerfile: src/Services/DocumentService/src/DocumentService.Api/Dockerfile
    ports:
      - "5001:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp"
      RabbitMq__ConnectionString: "amqp://guest:guest@rabbitmq:5672"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:8080/health/live || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 5

  datasource-service:
    build:
      context: ..
      dockerfile: src/Services/DataSourceService/src/DataSourceService.Api/Dockerfile
    ports:
      - "5002:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp"
      RabbitMq__ConnectionString: "amqp://guest:guest@rabbitmq:5672"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy

  conversion-service:
    build:
      context: ..
      dockerfile: src/Services/ConversionService/src/ConversionService.Worker/Dockerfile
    environment:
      DOTNET_ENVIRONMENT: Development
      RabbitMq__ConnectionString: "amqp://guest:guest@rabbitmq:5672"
      Otlp__Endpoint: "http://otel-collector:4317"
    depends_on:
      rabbitmq:
        condition: service_healthy

  ingestion-service:
    build:
      context: ..
      dockerfile: src/Services/IngestionService/src/IngestionService.Worker/Dockerfile
    environment:
      DOTNET_ENVIRONMENT: Development
      RabbitMq__ConnectionString: "amqp://guest:guest@rabbitmq:5672"
      Otlp__Endpoint: "http://otel-collector:4317"
    depends_on:
      rabbitmq:
        condition: service_healthy

  retrieval-service:
    build:
      context: ..
      dockerfile: src/Services/RetrievalService/src/RetrievalService.Api/Dockerfile
    ports:
      - "5003:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=retrieval_svc;Username=kp;Password=kp"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy
      qdrant:
        condition: service_healthy

  aianalysis-service:
    build:
      context: ..
      dockerfile: src/Services/AiAnalysisService/src/AiAnalysisService.Api/Dockerfile
    ports:
      - "5004:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=aianalysis_svc;Username=kp;Password=kp"
      RabbitMq__ConnectionString: "amqp://guest:guest@rabbitmq:5672"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy

  authorization-service:
    build:
      context: ..
      dockerfile: src/Services/AuthorizationService/src/AuthorizationService.Api/Dockerfile
    ports:
      - "5005:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=authz_svc;Username=kp;Password=kp"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy

  wiki-service:
    build:
      context: ..
      dockerfile: src/Services/WikiService/src/WikiService.Api/Dockerfile
    ports:
      - "5006:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
    depends_on:
      postgres:
        condition: service_healthy

  llm-gateway:
    build:
      context: ..
      dockerfile: src/Gateway/LlmGateway/src/LlmGateway.Api/Dockerfile
    ports:
      - "5007:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
      Llm__Claude__ApiKey: "${CLAUDE_API_KEY:-}"

  bff:
    build:
      context: ..
      dockerfile: src/Bff/KnowledgePlatform.Bff/Dockerfile
    ports:
      - "5000:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      Redis__ConnectionString: "redis:6379"
      Otlp__Endpoint: "http://otel-collector:4317"
      Auth__Authority: "http://keycloak:8080/realms/knowledge-platform"
      Services__RetrievalService: "http://retrieval-service:8080"
      Services__AiAnalysisService: "http://aianalysis-service:8080"
      Services__DocumentService: "http://document-service:8080"
      Services__AuthorizationService: "http://authorization-service:8080"
      Services__WikiService: "http://wiki-service:8080"
    depends_on:
      redis:
        condition: service_healthy

volumes:
  postgres_data:
  rabbitmq_data:
  redis_data:
  qdrant_data:
  prometheus_data:
  loki_data:
  tempo_data:
  grafana_data:
```

- [ ] **Create `deploy/otel-collector-config.yaml`**

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

exporters:
  prometheus:
    endpoint: "0.0.0.0:8889"
  otlp/tempo:
    endpoint: "tempo:4317"
    tls:
      insecure: true
  loki:
    endpoint: "http://loki:3100/loki/api/v1/push"
  debug:
    verbosity: normal

processors:
  batch:
    timeout: 1s

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlp/tempo, debug]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheus]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [loki]
```

- [ ] **Create `deploy/prometheus.yml`**

```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: otel-collector
    static_configs:
      - targets: ["otel-collector:8889"]
```

- [ ] **Create `deploy/tempo.yaml`**

```yaml
server:
  http_listen_port: 3200

distributor:
  receivers:
    otlp:
      protocols:
        grpc:
          endpoint: 0.0.0.0:4317

storage:
  trace:
    backend: local
    local:
      path: /tmp/tempo/blocks
```

- [ ] **Create `deploy/init-db.sql`** (create schemas per service)

```sql
-- ADR-0002: Database per Service — one schema per service in local dev
CREATE DATABASE document_svc;
CREATE DATABASE datasource_svc;
CREATE DATABASE retrieval_svc;
CREATE DATABASE aianalysis_svc;
CREATE DATABASE authz_svc;
CREATE DATABASE wiki_svc;
```

Wait — PostgreSQL `CREATE DATABASE` can't run inside a transaction or init script that already has a DB. Use a different approach:

```sql
-- deploy/init-db.sql
-- Creates schemas in the default 'kp' database for local dev
-- In production (k3s), each service gets its own PostgreSQL instance
CREATE SCHEMA IF NOT EXISTS document_svc;
CREATE SCHEMA IF NOT EXISTS datasource_svc;
CREATE SCHEMA IF NOT EXISTS retrieval_svc;
CREATE SCHEMA IF NOT EXISTS aianalysis_svc;
CREATE SCHEMA IF NOT EXISTS authz_svc;
CREATE SCHEMA IF NOT EXISTS wiki_svc;
```

And update all connection strings in docker-compose to use `Database=kp` with `SearchPath={schema}` pattern, or simply use separate databases. For simplicity in P0, use a single DB named `kp` with schema-per-service approach.

Actually, the cleanest approach for local dev is to use `POSTGRES_MULTIPLE_DATABASES`. Use this init script instead:

```bash
# deploy/create-multiple-dbs.sh
#!/bin/bash
set -e
for DB in document_svc datasource_svc retrieval_svc aianalysis_svc authz_svc wiki_svc; do
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-EOSQL
        CREATE DATABASE $DB;
    EOSQL
done
```

Update `docker-compose.yml` postgres volumes to mount this script:
```yaml
volumes:
  - ./create-multiple-dbs.sh:/docker-entrypoint-initdb.d/create-multiple-dbs.sh:ro
```

- [ ] **Commit**

```bash
git add deploy/
git commit -m "chore: add docker-compose local dev environment with all infrastructure services"
```

---

### Task 11: Helm chart skeletons

**Files:**
- Create: `deploy/helm/knowledge-platform/Chart.yaml`
- Create: `deploy/helm/knowledge-platform/values.yaml`
- Create: `deploy/helm/charts/document-service/Chart.yaml`
- Create: `deploy/helm/charts/document-service/values.yaml`
- Create: `deploy/helm/charts/document-service/templates/deployment.yaml`
- Create: `deploy/helm/charts/document-service/templates/service.yaml`
- Create: `deploy/helm/charts/document-service/templates/configmap.yaml`

(Repeat for all 10 services — same pattern)

- [ ] **Create parent chart**

```yaml
# deploy/helm/knowledge-platform/Chart.yaml
apiVersion: v2
name: knowledge-platform
description: Social knowledge platform (microservices)
type: application
version: 0.1.0
appVersion: "0.1.0"
dependencies:
  - name: document-service
    version: "0.1.0"
    repository: "file://../charts/document-service"
  - name: datasource-service
    version: "0.1.0"
    repository: "file://../charts/datasource-service"
  - name: conversion-service
    version: "0.1.0"
    repository: "file://../charts/conversion-service"
  - name: ingestion-service
    version: "0.1.0"
    repository: "file://../charts/ingestion-service"
  - name: retrieval-service
    version: "0.1.0"
    repository: "file://../charts/retrieval-service"
  - name: aianalysis-service
    version: "0.1.0"
    repository: "file://../charts/aianalysis-service"
  - name: authorization-service
    version: "0.1.0"
    repository: "file://../charts/authorization-service"
  - name: wiki-service
    version: "0.1.0"
    repository: "file://../charts/wiki-service"
  - name: bff
    version: "0.1.0"
    repository: "file://../charts/bff"
  - name: llm-gateway
    version: "0.1.0"
    repository: "file://../charts/llm-gateway"
```

- [ ] **Create document-service chart (template for all)**

```yaml
# deploy/helm/charts/document-service/Chart.yaml
apiVersion: v2
name: document-service
description: Document management service
type: application
version: 0.1.0
appVersion: "0.1.0"
```

```yaml
# deploy/helm/charts/document-service/values.yaml
replicaCount: 1
image:
  repository: harbor.internal/knowledge-platform/document-service
  pullPolicy: IfNotPresent
  tag: "latest"
service:
  type: ClusterIP
  port: 8080
env:
  ASPNETCORE_ENVIRONMENT: Production
  Otlp__Endpoint: "http://otel-collector.monitoring.svc.cluster.local:4317"
  Auth__Authority: "http://keycloak.auth.svc.cluster.local:8080/realms/knowledge-platform"
resources:
  requests:
    memory: "128Mi"
    cpu: "100m"
  limits:
    memory: "512Mi"
    cpu: "500m"
```

```yaml
# deploy/helm/charts/document-service/templates/deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}-document-service
  labels:
    app: document-service
    version: {{ .Values.image.tag }}
spec:
  replicas: {{ .Values.replicaCount }}
  selector:
    matchLabels:
      app: document-service
  template:
    metadata:
      labels:
        app: document-service
        version: {{ .Values.image.tag }}
    spec:
      containers:
        - name: document-service
          image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          ports:
            - containerPort: 8080
          envFrom:
            - configMapRef:
                name: {{ .Release.Name }}-document-service-config
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 15
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 15
            periodSeconds: 15
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
```

```yaml
# deploy/helm/charts/document-service/templates/service.yaml
apiVersion: v1
kind: Service
metadata:
  name: {{ .Release.Name }}-document-service
spec:
  selector:
    app: document-service
  ports:
    - port: {{ .Values.service.port }}
      targetPort: 8080
  type: {{ .Values.service.type }}
```

```yaml
# deploy/helm/charts/document-service/templates/configmap.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-document-service-config
data:
  {{- range $k, $v := .Values.env }}
  {{ $k }}: {{ $v | quote }}
  {{- end }}
```

- [ ] **Create charts for all remaining services** (same pattern, change name):
  - `datasource-service` (port 8080, env same pattern)
  - `conversion-service` (no HTTP port — Worker)
  - `ingestion-service` (no HTTP port — Worker)
  - `retrieval-service`
  - `aianalysis-service`
  - `authorization-service`
  - `wiki-service`
  - `bff`
  - `llm-gateway`

For Worker services (no HTTP), remove `livenessProbe`/`readinessProbe` from deployment template.

- [ ] **Commit**

```bash
git add deploy/helm/
git commit -m "chore(ADR-0007): add Helm chart skeletons for all services"
```

---

### Task 12: CI/CD activation

**Files:**
- Rename: `.github/workflows/ci.example.yml` → `.github/workflows/ci.yml`
- Rename: `.github/workflows/codeql.example.yml` → `.github/workflows/codeql.yml`

- [ ] **Activate CI workflow**

```bash
cp .github/workflows/ci.example.yml .github/workflows/ci.yml
```

Edit `.github/workflows/ci.yml` to add the actual build/test steps for .NET:

```yaml
name: CI

on:
  push:
    branches: [develop, main]
  pull_request:
    branches: [develop, main]

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Restore
        run: dotnet restore src/KnowledgePlatform.sln

      - name: Build
        run: dotnet build src/KnowledgePlatform.sln --no-restore -c Release

      - name: Test
        run: |
          dotnet test src/KnowledgePlatform.sln \
            --no-build -c Release \
            --logger "trx;LogFileName=results.trx" \
            --collect:"XPlat Code Coverage" \
            -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura

      - name: Upload test results
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: "**/*.trx"

      - name: Upload coverage
        uses: actions/upload-artifact@v4
        with:
          name: coverage
          path: "**/coverage.cobertura.xml"
```

- [ ] **Verify security.yml is correct** (already exists)

```bash
cat .github/workflows/security.yml
```

- [ ] **Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "chore(ADR-0007): activate CI workflow for .NET 8 build and test"
```

---

### Task 13: Spec documents fulfillment

**Files:**
- Modify: `docs/tech/tech-requirements.md`
- Modify: `docs/security/security.md`
- Modify: `docs/operations/operations.md`

- [ ] **Update tech-requirements.md** — fill in all table cells with actual values from ADR-0001–0014 and the implementation decisions made in Tasks 1–12

Key values:
```
Language: C# 12 / .NET 8
Framework: ASP.NET Core 8 Minimal APIs
Data store: PostgreSQL 16 (EF Core 8), Qdrant (vector), Redis 7
Infra: Kubernetes k3s (prod), docker-compose (local dev)
```

- [ ] **Update security.md** — fill in auth/authz/encryption rows from ADR-0004/ADR-0005

- [ ] **Update operations.md** — fill in deploy/monitoring rows from ADR-0006/ADR-0007

- [ ] **Commit**

```bash
git add docs/tech/ docs/security/ docs/operations/
git commit -m "docs: fill in tech requirements, security, and operations spec documents"
```

---

### Task 14: Final verification

- [ ] **Build entire solution**

```bash
cd src
dotnet build KnowledgePlatform.sln -c Release
```
Expected: `Build succeeded. 0 Error(s).`

- [ ] **Run all tests**

```bash
cd src
dotnet test KnowledgePlatform.sln -v minimal
```
Expected: All tests pass. Output shows test counts per project.

- [ ] **Verify docker-compose syntax**

```bash
cd deploy
docker compose config --quiet
```
Expected: No errors.

- [ ] **Final commit**

```bash
git add .
git commit -m "chore: P0 foundation complete — all services skeleton, CI/CD, docker-compose, Helm"
```
