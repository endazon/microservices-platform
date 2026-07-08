using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using KnowledgePlatform.Shared.Infrastructure.Composable.Adapters.Storage;
using ConversionService.Worker.Composable.Steps;
using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Services;
using ConversionService.Worker.Foundation.Domain;
using ConversionService.Worker.Composable.Adapters;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Serilog;

const string ServiceName = "knowledge-platform.conversion-service";

// FR-15, IADR-0029: 自己申告エンドポイントの最小 HTTP サーフェスのため WebApplication を用いる。
// MassTransit コンシューマ（変換ワーカー）は従来どおり IHostedService として稼働する。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((sp, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(builder.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);

// FR-12, ADR-0012: 本文変換（pandoc ラッパー）。
builder.Services.AddSingleton<IBodyConverter, PandocConversionService>();

// FR-12, ADR-0014/ADR-0015, IADR-0024: 正規化本文・資産の S3 互換オブジェクトストレージ（MinIO）保管。
// 共有クライアントを登録し、起動時にバケット存在・バージョニングを保証する。
builder.Services.AddKnowledgePlatformObjectStorage(builder.Configuration);
builder.Services.AddKnowledgePlatformObjectStorageBootstrap();
builder.Services.AddSingleton<IObjectStore, StorageObjectStore>();

// FR-12, ADR-0012/0010: 図のコード化（LLMゲートウェイ経由、機密区分で送信制御）。
builder.Services.AddHttpClient<IDiagramCoder, LlmGatewayDiagramCoder>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// FR-12, UC-06: 正規化オーケストレータ（本文＋図＋保管を束ねる）。
builder.Services.AddScoped<INormalizationService, NormalizationService>();

// ADR-0003: MassTransit
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddKnowledgePlatformPipelineConfig();
var pipeline = builder.Configuration.GetKnowledgePlatformPipeline();

// FR-15, ADR-0018, IADR-0029: 自己申告（イントロスペクション）— この段（convert）の実効値を申告する。
// これによりドリフト検出でワーカー段が Verifiable となり、適用漏れ（MissingApply）を検出できる。
builder.Services.AddKnowledgePlatformIntrospection("conversion-service", pipeline,
    i => i.AddStep<RawDocumentFetchedConsumer>());

builder.Services.AddMassTransit(x =>
{
    x.AddKnowledgePlatformPipelineStep<RawDocumentFetchedConsumer>(pipeline);
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // FR-12, UC-06 例外フロー / ADR-0003: 変換失敗（pandoc エラー・保存失敗）は再試行する。
        // 再試行を使い切った継続失敗は MassTransit が自動で <queue>_error（デッドレター）へ送る（共通設定）。
        cfg.UseKnowledgePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

// FR-15, IADR-0029: 自己申告エンドポイント（GET /internal/introspection）。
// メッシュ内部限定（ingress へ公開しない。IADR-0017 ネットワーク分離 / IADR-0026 mTLS が防御）。
app.MapKnowledgePlatformIntrospection();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
