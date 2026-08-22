using GraphService.Api.Foundation.Endpoints;
using GraphService.Api.Foundation.Persistence;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "microservices-platform.graph-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=graph_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-17, ADR-0002, ADR-0033 決定 1: GraphService 専用 DbContext（DB-per-service）
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=graph_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<GraphDbContext>(opt => opt.UseNpgsql(connStr));

// FR-17, FR-05, ADR-0004, ADR-0034: ABAC 許可スコープの解決先。
// WikiService / AiAnalysisService / Platform.Bff と同じ名前付き HttpClient を使う。
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
builder.Services.AddScoped<IGraphAccessResolver, GraphAccessResolver>();
builder.Services.AddScoped<IGraphStore, EfGraphStore>();
// UC-10: ホップごと判定を守る近傍探索（#909）。
builder.Services.AddScoped<GraphTraversal>();

// FR-15, ADR-0018: 自己申告（イントロスペクション）。段・合成可能ポートはまだホストしない。
builder.Services.AddPlatformIntrospection("graph-service", new PipelineOptions());

var app = builder.Build();

// FR-17: 起動時にスキーマを最新 Migration へ更新し、辺の型の初期値集合を投入する。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
    // ADR-0033 決定 3: seed は「空の辞書で始めない」ためのもので、
    // 既存の型（SC-09 で改名され得る）には一切触らない。冪等。
    await EdgeTypeSeed.EnsureSeededAsync(db);
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapGraphEndpoints();

app.Run();

public partial class Program { }
