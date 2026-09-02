using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Qdrant.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetrievalService.Common.Observability;
using RetrievalService.Features.McpTools;
using RetrievalService.Features.McpTools.Declare;
using RetrievalService.Features.Search;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Features.Search.RemoveDeleted;
using Wolverine;
using Wolverine.RabbitMQ;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;

const string ServiceName = "microservices-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
var qdrantHealthUri = new Uri(
    $"http://{builder.Configuration["Qdrant:Host"] ?? "qdrant"}:6333/healthz");
builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / #1016: Wolverine 購読側（retrieval-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddUrlGroup(qdrantHealthUri, "qdrant", tags: ["ready"])
    // FR-03, NFR-06, #1116 / [[IADR-0318]] 決定 3: **全文ペイロードインデックスの有無**を readiness へ載せる。
    // 🔴 Qdrant への疎通（上の "qdrant"）が緑でも、索引が無ければキーワード検索は
    // 全文検索として機能しない。しかも**例外が出ないので応答からもログからも分からない**。
    // ここが唯一の運用上の検出点である。**Degraded 止まり**（検索全体は落とさない。NFR-06）。
    .AddCheck<QdrantFullTextIndexHealthCheck>(
        QdrantFullTextIndexHealthCheck.Name,
        failureStatus: HealthStatus.Degraded, tags: ["ready"])
    // FR-03, #1118 / [[IADR-0331]] 決定 3: 日本語 2-gram（`text_ngram`）の索引の有無も同型で載せる。
    // `text` の索引が在っても、こちらが無ければ**日本語の語だけが 0 件**になる（識別子は当たる）。
    .AddCheck<QdrantCjkNgramIndexHealthCheck>(
        QdrantCjkNgramIndexHealthCheck.Name,
        failureStatus: HealthStatus.Degraded, tags: ["ready"]);
builder.Services.AddOpenApi();

// ADR-0009: Qdrant ベクトルDB クライアント
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "qdrant";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

// FR-03, SC-10, #1116 / [[IADR-0318]] 決定 3: 全文（キーワード）側の縮退を数える（0 が正常）。
// **応答へは載せない**（存在秘匿・[[IADR-0313]] 決定 1）。観測は応答の外側に置く。
builder.Services.AddSingleton<KeywordSearchMetrics>();

// ADR-0013: 埋め込みサービス（LLM ゲートウェイ経由）
builder.Services.AddHttpClient<IEmbeddingService, LlmGatewayEmbeddingService>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 RRF 統合）
builder.Services.AddScoped<HybridSearchService>();

// FR-04, FR-14, FR-17, UC-10, ADR-0035 決定 1・2, ADR-0018 (#970): 二段検索の段（グラフ近傍展開）。
//
// 🔴 **既定オフ・opt-in である**（ADR-0035 決定 2）。既定では段の型を **DI に登録しない** ——
// フラグを見て中で分岐するのではなく、**構成そのものが素のハイブリッド検索に戻る**。
// これが ADR-0018 / FR-14 の「着脱可能な段」の実現形であり、既存 RAG との A/B 比較の単位である。
//
// **`pipeline.json` の段としては宣言しない。** あの機構（`AddPlatformWolverineStep`）は
// 入力イベント型を持つ**購読段**専用であり、同期の検索経路には入力イベントが無い。
// 載せるには存在しないイベント型を捏造することになり、`input` 照合が意味を失う。
var graphExpansion = builder.Configuration
    .GetSection(GraphExpansionOptions.SectionName).Get<GraphExpansionOptions>()
    ?? new GraphExpansionOptions();
builder.Services.AddSingleton(graphExpansion.Normalize());

var graphServiceUrl = builder.Configuration["Services:GraphService"] ?? "http://graph-service:8080";

if (graphExpansion.Enabled)
{
    // ADR-0034: 権限伝播は `Authorization` ヘッダ（方式 A）。呼び出し元の JWT を下流へ運ぶため、
    // 要求文脈へ触れる必要がある。
    builder.Services.AddHttpContextAccessor();
    // 🔴 **名前リテラル＋インライン既定値の確立形で書く**（Platform.Bff / AiAnalysisService と同形）。
    // `scripts/check-bff-downstreams.js` の parseProgramDefaults がこの形から既定 URL を導出して
    // デプロイ manifest と突合する（#970 で RetrievalService も CALLERS 入り）。名前は
    // `GraphServiceNeighborExpander.ClientName`（"GraphService"）と一致させること。
    // 既定は :8080（メッシュ内の実 Service ポート。後発サービスの規約 —— Platform.Bff の同名 client 参照）。
    builder.Services.AddHttpClient("GraphService", c =>
        c.BaseAddress = new Uri(builder.Configuration["Services:GraphService"]
            ?? "http://graph-service:8080"));
    builder.Services.AddScoped<IGraphNeighborExpander, GraphServiceNeighborExpander>();
    builder.Services.AddScoped<IHybridSearchService, GraphExpandingSearchService>();
}
else
{
    builder.Services.AddScoped<IHybridSearchService>(sp => sp.GetRequiredService<HybridSearchService>());
}

// FR-14, ADR-0018 / #1016: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// 🔴 ADR-0027, ADR-0057 / #1016: DocumentDeleted を購読し、検索索引から当該文書のチャンクを削除する。
// 本サービス初のメッセージング導入であり、最初から Wolverine である（MassTransit は選べない ——
// backend-library-baseline 非掲載のため新規参照は即 fail。ADR-0030）。
// NFR, ADR-0027, #1022: ブローカ接続。**既定資格情報をイメージへ焼かない** —— appsettings.json からも
// 撤去したため、構成が注入されていなければここで落ちる（注入漏れが「既定の資格情報で接続成功」へ
// 倒れない。#1012 / IADR-0286 の DB と同型。IADR-0291）。**1 サービス 1 解決点にする。**
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（k8s は helm の global.messaging、compose は x-rabbit-env が注入する）。"
        + " 既定値は持たない —— 未注入をブローカへの接続失敗として現れさせないためである。");

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "retrieval-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var step = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);

    var retrievalQueue = step?.Queue ?? nameof(DocumentDeleted);

    // 手順 3（購読側の束ね）/ #992: 自分のキューをイベント型名の fan-out exchange へ束ねる。
    // **キュー名を分けるだけでは何も届かない** —— 束ねて初めて発行が届く。
    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision()
        .BindPlatformQueue<DocumentDeleted>("retrieval-service", retrievalQueue);

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う
    // （fan-out の保存: wiki-service / graph-service と別キューになりサービス名前置で分かれる）。
    opts.ListenToPlatformQueue("retrieval-service", retrievalQueue);

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。retrieval-delete 段（#1016）と、
// 選択中の合成可能ポート（ベクトルDB・埋め込み）を申告する。メッシュ内部限定で公開する。
// FR-04, FR-17 (#970): 二段検索の段は**有効なときだけ**ポートとして申告する。
// **段が入っているかどうかを外から読めること**が A/B 比較の前提である
// （応答は同じ形なので、結果だけを見ても段の有無は判らない）。
builder.Services.AddPlatformIntrospection("retrieval-service", pipeline,
    i =>
    {
        i.AddWolverineStep<DocumentDeletedConsumer>();
        i.AddPort("vector-store", nameof(QdrantVectorStore), $"qdrant:{qdrantPort}")
         .AddPort("embedding", nameof(LlmGatewayEmbeddingService), "llm-gateway");

        if (graphExpansion.Enabled)
            i.AddPort("graph-expansion", nameof(GraphServiceNeighborExpander), graphServiceUrl);
    });

var app = builder.Build();

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapSearchEndpoints();
// FR-16, ADR-0024 §2: MCP ツール定義の自己申告（メッシュ内部限定。#1020）。
app.MapMcpToolEndpoints();

app.Run();

public partial class Program { }
