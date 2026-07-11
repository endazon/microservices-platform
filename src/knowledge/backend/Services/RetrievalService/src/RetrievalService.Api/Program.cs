using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Qdrant.Client;
using RetrievalService.Api.Foundation.Ports;
using RetrievalService.Api.Foundation.Endpoints;
using RetrievalService.Api.Composable.Adapters;
using RetrievalService.Api.Foundation.Services;
using Serilog;

const string ServiceName = "microservices-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
var qdrantHealthUri = new Uri(
    $"http://{builder.Configuration["Qdrant:Host"] ?? "qdrant"}:6333/healthz");
builder.Services.AddPlatformHealthChecks()
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

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段はホストしないが、
// 選択中の合成可能ポート（ベクトルDB・埋め込み）を申告する。メッシュ内部限定で公開する。
builder.Services.AddPlatformIntrospection("retrieval-service", new PipelineOptions(),
    i => i
        .AddPort("vector-store", nameof(QdrantVectorStore), $"qdrant:{qdrantPort}")
        .AddPort("embedding", nameof(LlmGatewayEmbeddingService), "llm-gateway"));

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapSearchEndpoints();

app.Run();

public partial class Program { }
