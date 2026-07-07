using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using Qdrant.Client;
using RetrievalService.Api.Foundation.Ports;
using RetrievalService.Api.Foundation.Endpoints;
using RetrievalService.Api.Composable.Adapters;
using RetrievalService.Api.Foundation.Services;
using Serilog;

const string ServiceName = "knowledge-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
var qdrantHealthUri = new Uri(
    $"http://{builder.Configuration["Qdrant:Host"] ?? "qdrant"}:6333/healthz");
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddUrlGroup(qdrantHealthUri, "qdrant", tags: ["ready"]);
builder.Services.AddOpenApi();

// ADR-0009: Qdrant ベクトルDB クライアント
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "qdrant";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
builder.Services.AddHttpClient<IEmbeddingService, LlmGatewayEmbeddingService>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 RRF 統合）
builder.Services.AddScoped<IHybridSearchService, HybridSearchService>();

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapSearchEndpoints();

app.Run();

public partial class Program { }
