using GraphService.Api.Composable.Adapters;
using GraphService.Api.Composable.Steps;
using GraphService.Api.Foundation.Endpoints;
using GraphService.Api.Foundation.Persistence;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.RabbitMQ;

const string ServiceName = "microservices-platform.graph-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / #1016: Wolverine 購読側（graph-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    // ⚠️ これにより本サービスの readiness はブローカへ依存する（#911 論点 1 の選択肢 3 =
    // 現状受容。判断の記録は作業仕様書 20260828_issue-1016_delete-propagation.md §readiness）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=graph_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-17, ADR-0002, ADR-0033 決定 1: GraphService 専用 DbContext（DB-per-service）
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=graph_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<GraphDbContext>(opt => opt.UseNpgsql(connStr));

// FR-17, FR-05, ADR-0004, ADR-0034: ABAC 許可スコープの解決先。
// WikiService / AiAnalysisService / Platform.Bff と同じ名前付き HttpClient を使う。
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
builder.Services.AddScoped<IGraphAccessResolver, GraphAccessResolver>();
builder.Services.AddScoped<IGraphStore, EfGraphStore>();
// UC-10: ホップごと判定を守る近傍探索（#909）。
builder.Services.AddScoped<GraphTraversal>();
// FR-18 (#914): 却下・解除の時刻。テストから固定できるよう TimeProvider を通す。
builder.Services.AddSingleton(TimeProvider.System);

// FR-18, ADR-0010, ADR-0034 決定 5, ADR-0051, IADR-0266 (#915): AI 提案の生成。
//
// 🔴 **LLM への送信は SuggestionPrompt（封）を通る経路しか無い。** 封の構築には
// AuthorizedNode と AccessScopeResponse の両方が要る（IADR-0266 決定 1）。
builder.Services.AddHttpClient<ISuggestionLlmClient, LlmGatewaySuggestionClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"]
        ?? "http://llm-gateway:5010"));
// ADR-0051 決定 1 が認めた「全文書横断の類似度」の口が RetrievalService に無いため、
// **既定は空を返すアダプタである**（fail-closed 側。IADR-0266 論点 C）。
builder.Services.AddScoped<ISimilarityCandidateSource, UnconfiguredSimilarityCandidateSource>();
builder.Services.AddScoped<AiSuggestionGenerator>();

// FR-14, ADR-0018 / #1016: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// 🔴 ADR-0027, ADR-0057 / #1016: DocumentDeleted を購読し、グラフ（ノード・辺・AI 提案）から
// 当該文書の痕跡を掃除する。ADR-0033 決定 2 / #911: DocumentUpdated を購読し、ABAC 属性を
// デノーマライズ保持する（graph-sync 段）。本サービス初のメッセージング導入であり、
// 最初から Wolverine である（MassTransit は選べない —— backend-library-baseline 非掲載のため
// 新規参照は即 fail。ADR-0030）。
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "graph-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var deleteStep = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);
    var syncStep = opts.AddPlatformWolverineStep<GraphDocumentSyncConsumer>(pipeline);

    opts.UseRabbitMq(new Uri(builder.Configuration["RabbitMq:ConnectionString"]
        ?? "amqp://guest:guest@rabbitmq:5672")).AutoProvision();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う
    // （fan-out の保存: 他購読サービスと別キューになりサービス名前置で分かれる）。
    // ハンドラへの振り分けはメッセージ型で決まる（キュー 2 本 → 同一ホスト内で型別ディスパッチ）。
    opts.ListenToPlatformQueue("graph-service", deleteStep?.Queue ?? nameof(DocumentDeleted));
    opts.ListenToPlatformQueue("graph-service", syncStep?.Queue ?? nameof(DocumentUpdated));

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

// FR-15, ADR-0018: 自己申告（イントロスペクション）— graph-delete 段（#1016）と
// graph-sync 段（#911）を申告する。
builder.Services.AddPlatformIntrospection("graph-service", pipeline, i => i
    .AddWolverineStep<DocumentDeletedConsumer>()
    .AddWolverineStep<GraphDocumentSyncConsumer>());

var app = builder.Build();

// FR-17: 起動時にスキーマを最新 Migration へ更新し、辺の型の初期値集合を投入する。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
    // ADR-0033 決定 3: seed は「空の辞書で始めない」ためのもので、
    // 既存の型（SC-09 で改名され得る）には一切触らない。冪等。
    await EdgeTypeSeed.EnsureSeededAsync(db);
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapGraphEndpoints();
// FR-17, SC-09, SC-10: 辺の型辞書（#910）。
app.MapEdgeTypeEndpoints();
// FR-18, SC-21, SC-03, ADR-0033 決定 7・10: AI 提案の 3 状態遷移（#914）。
// **一括承認の口は無い**（FR-18 / SC-21「描いてはいけないもの」）。
app.MapAiSuggestionEndpoints();

app.Run();

public partial class Program { }
