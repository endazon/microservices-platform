using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using KnowledgePlatform.Bff.Foundation.Endpoints;
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
builder.AddKnowledgePlatformConfigInspection();

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

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapSearchBffEndpoints();
app.MapAnalysisBffEndpoints();
app.MapFeedbackBffEndpoints();
app.MapDashboardBffEndpoints();
app.MapConfigBffEndpoints();

app.Run();

public partial class Program { }
