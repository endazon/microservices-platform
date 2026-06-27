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

// FR-02: 起動時に検索インデックス（Qdrant コレクション）の存在を保証する
builder.Services.AddHostedService<QdrantBootstrapHostedService>();

// FR-02 parse: 本文（Markdown）取得（http(s) は実取得、それ以外はプレースホルダー）
builder.Services.AddHttpClient<IDocumentContentReader, StorageDocumentContentReader>();

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
