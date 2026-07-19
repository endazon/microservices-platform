using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Bff.Composition;
using Serilog;

const string ServiceName = "microservices-platform.bff";

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

// Issue #283, AST/FR-17, AST/UC-06, IADR-0070: AST 設定画面（全体前提条件）の集約用。ConfigurationService(/assumptions)
// へ pass-through する。AST 未デプロイ時は不達（BFF が 502 へ縮退）で足を引かないよう、readiness の
// UriHealthCheck には含めない（可変ユニットの導入有無で BFF の可用性を左右させない・fail-safe）。
builder.Services.AddHttpClient("ConfigurationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:ConfigurationService"]
        ?? "http://configuration-service:8080"));

// Issue #287, FR-14, IADR-0071: AST リスク設定（SC-02）・統制状態参照（SC-03）の集約用。RiskManagementService
// (/risk-controls/*) へ pass-through する。ConfigurationService と同じく、AST 未デプロイ時の不達で BFF の可用性を
// 左右させないよう readiness の UriHealthCheck には含めない（fail-safe）。
builder.Services.AddHttpClient("RiskManagementService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RiskManagementService"]
        ?? "http://risk-management-service:8080"));

// Issue #288, FR-14, IADR-0072: AST 監視銘柄（SC-02 watchlist）の集約用。MarketMonitorService
// (/monitor/*) へ pass-through する。ConfigurationService/RiskManagementService と同じく、AST 未デプロイ時の
// 不達で BFF の可用性を左右させないよう readiness の UriHealthCheck には含めない（fail-safe）。
builder.Services.AddHttpClient("MarketMonitorService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:MarketMonitorService"]
        ?? "http://market-monitor-service:8080"));

// FR-06, ADR-0014/ADR-0015: 正規化 Markdown 本文の読み取り用オブジェクトストレージ（storage://）。
// 未構成時は NullObjectStorageClient（CanResolve=false）へ縮退し、本文はプレースホルダへフォールバックする。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapOpenApi();

// FR-14, IADR-0063: BFF エンドポイントは合成点（BffEndpointComposition）経由で一括登録する。
// ユニット追加時は合成点の登録簿へ 1 行追加するだけで組み込める（Program.cs は不変）。
app.MapComposedBffEndpoints();

app.Run();

public partial class Program { }
