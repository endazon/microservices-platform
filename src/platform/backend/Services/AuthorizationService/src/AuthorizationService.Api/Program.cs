using AuthorizationService.Api.Foundation.Endpoints;
using AuthorizationService.Api.Foundation.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "microservices-platform.authorization-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=authz_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-05: ABAC DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=authz_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<AuthorizationDbContext>(opt => opt.UseNpgsql(connStr));

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段・合成可能ポートは
// ホストしないが、到達可能性とトポロジ（段なし）を実効構成へ与えるため存在申告する。
builder.Services.AddPlatformIntrospection("authorization-service", new PipelineOptions());

var app = builder.Build();

// FR-05: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapAuthzEndpoints();

app.Run();

public partial class Program { }
