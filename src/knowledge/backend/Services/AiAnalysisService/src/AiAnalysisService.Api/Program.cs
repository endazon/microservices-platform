using AiAnalysisService.Api.Foundation.Endpoints;
using AiAnalysisService.Api.Foundation.Services;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Serilog;

const string ServiceName = "microservices-platform.aianalysis-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:RetrievalService"] ?? "http://retrieval-service:5003") + "/health/live"),
        "retrieval-service", tags: ["ready"])
    .AddUrlGroup(
        new Uri((builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007") + "/health/live"),
        "llm-gateway", tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-04: HTTP クライアント設定（サービス間通信）
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
builder.Services.AddHttpClient("RetrievalService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RetrievalService"]
        ?? "http://retrieval-service:5003"));
builder.Services.AddHttpClient("LlmGateway", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"]
        ?? "http://llm-gateway:5007"));

// FR-04: RAG オーケストレーター
builder.Services.AddScoped<IRagOrchestrator, RagOrchestrator>();

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。RAG オーケストレータは
// 他サービスを HTTP で束ねるため合成可能ポートを選択しない。到達可能性とトポロジを与えるため存在申告する。
builder.Services.AddPlatformIntrospection("aianalysis-service", new PipelineOptions());

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapAnalysisEndpoints();

app.Run();

public partial class Program { }
