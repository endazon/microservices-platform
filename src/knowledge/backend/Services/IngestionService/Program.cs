using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion;
using Knowledge.Contracts.Events;
using IngestionService.Domain;
using IngestionService.Infrastructure.ExternalServices;
using IngestionService.Infrastructure.Messaging;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Qdrant.Client;
using Wolverine;
using Wolverine.RabbitMQ;

const string ServiceName = "microservices-platform.ingestion-service";

// FR-15, IADR-0029: 自己申告エンドポイントの最小 HTTP サーフェスのため WebApplication を用いる。
// MassTransit コンシューマ（取り込みワーカー）は従来どおり IHostedService として稼働する。
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

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
builder.Services.AddPlatformObjectStorage(builder.Configuration);

// FR-02/FR-06 parse: 本文（Markdown）取得（storage:// はオブジェクトストレージ、http(s) は実取得、
// それ以外はプレースホルダー）
builder.Services.AddHttpClient<IDocumentContentReader, StorageDocumentContentReader>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
builder.Services.AddHttpClient<IEmbeddingService, LlmGatewayEmbeddingService>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018, IADR-0029: 自己申告（イントロスペクション）— この段（ingest）の実効値を申告する。
// これによりドリフト検出でワーカー段が Verifiable となり、適用漏れ（MissingApply）を検出できる。
// ingest は Wolverine 段（E3b）なので AddWolverineStep で申告する。
builder.Services.AddPlatformIntrospection("ingestion-service", pipeline,
    i => i.AddWolverineStep<DocumentUpdatedConsumer>());

// NFR, ADR-0027, #1022: ブローカ接続。**既定資格情報をイメージへ焼かない** —— appsettings.json からも
// 撤去したため、構成が注入されていなければここで落ちる（注入漏れが「既定の資格情報で接続成功」へ
// 倒れない。#1012 / IADR-0286 の DB と同型。IADR-0291）。**1 サービス 1 解決点にする。**
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（k8s は helm の global.messaging、compose は x-rabbit-env が注入する）。"
        + " 既定値は持たない —— 未注入をブローカへの接続失敗として現れさせないためである。");

// 🔴 ADR-0027 / E3b: **ingest 段（DocumentUpdated 購読）は Wolverine へ移した。**
// MassTransit に残るのは IngestionCompleted の**発行だけ**（その辺は本 PR の射程外。
// 辺は原子的に動かす —— IADR-0234 決定 3）。移行期間中 **両スタックを同居させる**
// （E1 の ConversionService と同じ形: Wolverine 購読 ＋ MassTransit 発行）。
builder.Services.AddScoped<IIngestionCompletedPublisher,
    MassTransitIngestionCompletedPublisher>();
builder.Services.AddMassTransit(x =>
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitConnection);
        cfg.UsePlatformRetry();
        cfg.ConfigureEndpoints(ctx);
    }));

// 購読側（DocumentUpdated）は Wolverine。
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "ingestion-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var step = opts.AddPlatformWolverineStep<DocumentUpdatedConsumer>(pipeline);

    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う
    // （fan-out の保存: wiki-service / graph-service と別キューになりサービス名前置で分かれる）。
    opts.ListenToPlatformQueue("ingestion-service", step?.Queue ?? nameof(DocumentUpdated));

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

var app = builder.Build();

// FR-15, IADR-0029: 自己申告エンドポイント（GET /internal/introspection）。
// メッシュ内部限定（ingress へ公開しない。IADR-0017 ネットワーク分離 / IADR-0026 mTLS が防御）。
app.MapPlatformIntrospection();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
