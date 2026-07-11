using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Bff.Foundation.Endpoints;
using Serilog;

const string ServiceName = "knowledge-platform.bff";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddRedis(
        builder.Configuration["Redis:ConnectionString"] ?? "redis:6379",
        tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:AiAnalysisService"] ?? "http://aianalysis-service:5004") + "/health/live"),
        "aianalysis-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:FeedbackService"] ?? "http://feedback-service:5008") + "/health/live"),
        "feedback-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:DashboardService"] ?? "http://dashboard-service:5009") + "/health/live"),
        "dashboard-service", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

// FR-15, ADR-0018: 構成情報 API（実効構成の集約・ドリフト定期検出・監査）を BFF へ同居させる。
builder.AddPlatformConfigInspection();

// FR-04, UC-01: AiAnalysisService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("AiAnalysisService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AiAnalysisService"]
        ?? "http://aianalysis-service:5004"));

// FR-08, UC-01: FeedbackService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("FeedbackService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:FeedbackService"]
        ?? "http://feedback-service:5008"));

// FR-10, UC-05: DashboardService 集約用の名前付き HTTP クライアント
builder.Services.AddHttpClient("DashboardService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DashboardService"]
        ?? "http://dashboard-service:5009"));

// FR-03, UC-01, SC-01: 横断検索の集約用。ABAC スコープ解決（AuthorizationService）→ 検索（RetrievalService）。
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
builder.Services.AddHttpClient("RetrievalService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RetrievalService"]
        ?? "http://retrieval-service:5003"));

// FR-06, UC-03/UC-07, SC-03: 文書閲覧の集約用。ABAC スコープ解決（AuthorizationService）→ 文書取得。
builder.Services.AddHttpClient("DocumentService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DocumentService"]
        ?? "http://document-service:5001"));

// FR-12, UC-06, SC-07: 変換ジョブ管理の集約用（管理者・運用者限定）。ワーカーの HTTP サーフェスは 8080。
builder.Services.AddHttpClient("ConversionService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:ConversionService"]
        ?? "http://conversion-service:8080"));

// FR-01, FR-02, UC-04, SC-06: データソース管理の集約用（管理者・運用者限定）。
builder.Services.AddHttpClient("DataSourceService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DataSourceService"]
        ?? "http://datasource-service:5002"));

// FR-06, ADR-0014/ADR-0015: 正規化 Markdown 本文の読み取り用オブジェクトストレージ（storage://）。
// 未構成時は NullObjectStorageClient（CanResolve=false）へ縮退し、本文はプレースホルダへフォールバックする。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapOpenApi();

app.MapSearchBffEndpoints();
app.MapDocumentBffEndpoints();
app.MapAnalysisBffEndpoints();
app.MapFeedbackBffEndpoints();
app.MapDashboardBffEndpoints();
app.MapConfigBffEndpoints();
app.MapConversionBffEndpoints();
app.MapAuthzBffEndpoints();
app.MapDataSourceBffEndpoints();

app.Run();

public partial class Program { }
