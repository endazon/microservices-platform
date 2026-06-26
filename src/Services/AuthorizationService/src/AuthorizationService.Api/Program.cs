using AuthorizationService.Api.Endpoints;
using AuthorizationService.Api.Infrastructure;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "knowledge-platform.authorization-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=authz_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-05: ABAC DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=authz_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<AuthorizationDbContext>(opt => opt.UseNpgsql(connStr));

var app = builder.Build();

// FR-05: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapAuthzEndpoints();

app.Run();

public partial class Program { }
