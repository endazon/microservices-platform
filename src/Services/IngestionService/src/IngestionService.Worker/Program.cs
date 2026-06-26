using IngestionService.Worker.Consumers;
using IngestionService.Worker.Services;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Qdrant.Client;
using Serilog;

const string ServiceName = "knowledge-platform.ingestion-service";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(builder.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);

// FR-02: チャンク化・埋め込み・ベクトルDB依存
builder.Services.AddSingleton<IChunkingService, MarkdownChunkingService>();

var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "qdrant";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IIngestionVectorStore, QdrantIngestionVectorStore>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
builder.Services.AddHttpClient<IEmbeddingService, LlmGatewayEmbeddingService>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// ADR-0003: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DocumentUpdatedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
