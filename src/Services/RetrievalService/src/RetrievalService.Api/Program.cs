using KnowledgePlatform.Shared.Infrastructure.Extensions;
using Qdrant.Client;
using RetrievalService.Api.Abstractions;
using RetrievalService.Api.Endpoints;
using RetrievalService.Api.Infrastructure;
using Serilog;

const string ServiceName = "knowledge-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddUrlGroup(
        new Uri((builder.Configuration["Qdrant:Endpoint"] ?? "http://qdrant:6334") + "/healthz"),
        "qdrant", tags: ["ready"]);
builder.Services.AddOpenApi();

// ADR-0009: Qdrant ベクトルDB クライアント
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "qdrant";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
builder.Services.AddHttpClient<IEmbeddingService, LlmGatewayEmbeddingService>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

var app = builder.Build();

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapSearchEndpoints();

app.Run();

public partial class Program { }
