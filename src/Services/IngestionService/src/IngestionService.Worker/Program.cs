using KnowledgePlatform.Shared.Infrastructure.Composable.Adapters.Storage;
using IngestionService.Worker.Composable.Steps;
using IngestionService.Worker.Foundation.Ports;
using IngestionService.Worker.Foundation.Domain;
using IngestionService.Worker.Composable.Adapters;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
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

// FR-02, ADR-0016: モデル別コレクション（voyage/1024・ruri/768）の定義（起動時作成・残存防止削除に使用）。
builder.Services.Configure<EmbeddingCollectionsOptions>(
    builder.Configuration.GetSection(EmbeddingCollectionsOptions.SectionName));
builder.Services.AddSingleton<IIngestionVectorStore, QdrantIngestionVectorStore>();

// FR-02: 起動時に検索インデックス（Qdrant コレクション）の存在を保証する
builder.Services.AddHostedService<QdrantBootstrapHostedService>();

// FR-06, ADR-0014/ADR-0015: オブジェクトストレージ（MinIO）クライアント（storage:// 本文の実取得用）。
builder.Services.AddKnowledgePlatformObjectStorage(builder.Configuration);

// FR-02/FR-06 parse: 本文（Markdown）取得（storage:// はオブジェクトストレージ、http(s) は実取得、
// それ以外はプレースホルダー）
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

        // ADR-0003: 取り込み（チャンク化・埋め込み・ベクトル登録）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UseKnowledgePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
