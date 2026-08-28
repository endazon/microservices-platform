using DashboardService.Api.Foundation.Endpoints;
using DashboardService.Api.Foundation.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "microservices-platform.dashboard-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR, #1012: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-10: Dashboard DbContext（DB-per-service, ADR-0002）
builder.Services.AddDbContext<DashboardDbContext>(opt => opt.UseNpgsql(connStr));

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段・合成可能ポートは
// ホストしないが、到達可能性とトポロジ（段なし）を実効構成へ与えるため存在申告する。
builder.Services.AddPlatformIntrospection("dashboard-service", new PipelineOptions());

// FR-10, FR-17, FR-18, SC-10, ADR-0004 (#443): ナレッジ健全性指標の閲覧を監査ログに残す
// （計画 §ナレッジ健全性の指標「閲覧は監査ログに記録する」）。
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

var app = builder.Build();

// FR-10: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapDashboardEndpoints();
app.MapKnowledgeHealthEndpoints();

app.Run();

public partial class Program { }
