using DashboardService.Features.Dashboard;
using DashboardService.Features.Dashboard.PurgeExpired;
using DashboardService.Features.Dashboard.RecordEvent;
using DashboardService.Features.KnowledgeHealth;
using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
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

// FR-10, SC-10, ADR-0071 決定 1, [[IADR-0357]] (#1197): 検索傾向の出現件数の下限。
// **配備時の構成で変更できる**（環境変数 `SearchTrend__MinimumCount`）。
// 🔴 **ValidateOnStart は付けない** —— 秘匿パラメータの打ち間違いで、利用イベントの記録
// （`POST /dashboard/events`）まで巻き添えに止める価値は無い。不正値は既定へ倒し、
// 照会のたびに警告を残す（`DashboardEndpoints.EffectiveMinCount`）。
builder.Services.Configure<SearchTrendOptions>(
    builder.Configuration.GetSection(SearchTrendOptions.SectionName));

// FR-10, SC-10, ADR-0072 決定 3, [[IADR-0367]] (#1198): 利用イベントの保持期間（90 日）の実施。
// **保持日数は構成キーを持たない** —— 集計の上限（`DashboardEndpoints.MaxDays`）そのものであり、
// 片方だけ動かせる形にしない（ADR-0072 §残るもの 末尾）。構成できるのは有無と間隔だけである。
// 🔴 **ここも ValidateOnStart は付けない** —— 掃除の間隔の打ち間違いで、利用イベントの記録と
// 集計まで巻き添えに止める価値は無い。不正値は既定へ倒し、起動時に警告を残す。
builder.Services.Configure<UsageRetentionOptions>(
    builder.Configuration.GetSection(UsageRetentionOptions.SectionName));
builder.Services.AddScoped<UsageEventRetention>();
builder.Services.AddHostedService<UsageRetentionHostedService>();

// FR-10, FR-17, FR-18, SC-10, ADR-0004 (#443): ナレッジ健全性指標の閲覧を監査ログに残す
// （計画 §ナレッジ健全性の指標「閲覧は監査ログに記録する」）。
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

// FR-10, FR-17, FR-18, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0376:
// 端点の入力検証。**アセンブリ走査（AddValidatorsFromAssembly）は使わない** —— 登録が暗黙になり、
// 検証器を消しても起動時には何も起きず、端点が黙って無検証になるためである。
// 1 行 1 検証器の明示登録なら、消したときにコンパイルか DI 解決で止まる。
builder.Services.AddScoped<IValidator<UsageEventRequest>, RecordUsageEventValidator>();
builder.Services.AddScoped<IValidator<KnowledgeHealthReportRequest>, ReportKnowledgeHealthValidator>();

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
