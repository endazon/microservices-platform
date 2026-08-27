using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Qdrant.Client;
using RetrievalService.Api.Foundation.Ports;
using RetrievalService.Api.Foundation.Endpoints;
using RetrievalService.Api.Composable.Adapters;
using RetrievalService.Api.Foundation.Services;

const string ServiceName = "microservices-platform.retrieval-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

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

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段はホストしないが、
// 選択中の合成可能ポート（ベクトルDB・埋め込み）を申告する。メッシュ内部限定で公開する。
// FR-04, FR-17 (#970): 二段検索の段は**有効なときだけ**ポートとして申告する。
// **段が入っているかどうかを外から読めること**が A/B 比較の前提である
// （応答は同じ形なので、結果だけを見ても段の有無は判らない）。
builder.Services.AddPlatformIntrospection("retrieval-service", new PipelineOptions(),
    i =>
    {
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

app.Run();

public partial class Program { }
