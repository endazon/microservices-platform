using GraphService.Infrastructure.ExternalServices;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Features.AiSuggestions;
using GraphService.Features.AiSuggestions.Generate;
using GraphService.Features.EdgeTypes;
using GraphService.Features.Graph;
using GraphService.Features.McpTools.Declare;
using GraphService.Common.Observability;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using GraphService.Domain;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
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

// FR-17, SC-10, ADR-0033 決定 3 / #912: 未定義の辺の型が related へ丸められた件数（0 が正常）。
builder.Services.AddMetrics();
builder.Services.AddSingleton<EdgeTypeFallbackMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(EdgeTypeFallbackMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR, #1012: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / #1016: Wolverine 購読側（graph-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    // ⚠️ これにより本サービスの readiness はブローカへ依存する（#911 論点 1 の選択肢 3 =
    // 現状受容。判断の記録は作業仕様書 20260828_issue-1016_delete-propagation.md §readiness）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-17, ADR-0002, ADR-0033 決定 1: GraphService 専用 DbContext（DB-per-service）
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

// FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, IADR-0364 (#1187 / #1014): DocumentService との
// 2 本の経路 —— 生成段が引くタグ辞書（`/internal/tags/names`。本サービス自身が読む）と、
// 承認の反映（`POST /documents/{id}/tags`。**承認者本人の資格を転送する**。サービスアカウントは持たない）。
//
// 接続先は `Services:DocumentService`。既定 `http://document-service:8080` は compose・helm の
// いずれでも Service 名・ポートと一致する（`DashboardService` と同じ形）。
// `IHttpContextAccessor` は反映側が要求の `Authorization` を読むために要る（`RagOrchestrator` と同型）。
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient(HttpDocumentTagWriter.ClientName, c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:DocumentService"]
        ?? "http://document-service:8080"));
builder.Services.AddScoped<IDocumentTagWriter, HttpDocumentTagWriter>();
builder.Services.AddScoped<ITagDictionaryReader, HttpTagDictionaryReader>();
// 生成段で辞書外として落としたタグ提案の件数（0 が正常）。Meter は EdgeTypeFallbackMetrics と同じ。
builder.Services.AddSingleton<TagSuggestionDropMetrics>();

// FR-17, FR-06, ADR-0015, ADR-0033 決定 3・6・8 (#912): リンク抽出と辺の差分更新。
//
// **バケットの作成（Bootstrap）はここでは行わない** —— 書き込み側の起動時保証は ConversionService が
// 担っており、本サービスは読み取り側である（DocumentService / WikiService と同じ形）。
// 未設定の dev/test では縮退クライアントが登録され、StorageContentReader は null を返して
// 抽出をスキップする（辺は触らない）。
builder.Services.AddPlatformObjectStorage(builder.Configuration);
builder.Services.AddHttpClient<IGraphContentReader, StorageContentReader>();
builder.Services.AddScoped<LinkEdgeSynchronizer>();

// FR-10, FR-17, FR-19, UC-05, SC-10, ADR-0002, ADR-0006, IADR-0265, [[IADR-0299]] (#443):
// ナレッジ健全性の観測値の**生産者**。受け口（DashboardService）は #443 で実装済みだが、
// **本番コードから送っている経路が 1 本も無かった**（呼んでいたのはテストだけ）。ここで塞ぐ。
//
// 接続先は `Services:DashboardService`。既定 `http://dashboard-service:8080` は compose・helm の
// いずれでも Service 名・ポートと一致するため**上書きは要らない**（DocumentService →
// notification-service と同じ形）。🔴 chart のキー `dashboard` を変えると Service 名が動き、
// **fail-open のため 502 にすらならず静かに報告が止まる**。
builder.Services.AddHttpClient(HttpKnowledgeHealthReporter.ClientName, c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:DashboardService"]
        ?? "http://dashboard-service:8080");
    // 🔴 既定の 100 秒のままにしない —— 受け口が応答しないと定期処理がその間止まる。
    c.Timeout = HttpKnowledgeHealthReporter.SendTimeout;
});
builder.Services.AddScoped<IKnowledgeHealthReporter, HttpKnowledgeHealthReporter>();
// FR-10, UC-05, SC-10, planning#494 決定 1・3, [[IADR-0353]] (#1186): 陳腐化のしきい値（既定 180 日）。
// **配備時の構成で変更できる**（環境変数 KnowledgeHealth__StaleDocumentThresholdDays）。
// 🔴 **ValidateOnStart を付けない** —— 不正値で起動を落とすと本サービスの DocumentUpdated /
// DocumentDeleted 購読ごと止まる。既定へ倒して警告を出す（HttpKnowledgeHealthReporter の
// fail-open と同じ向き。倒した後の値がそのまま画面へ出る）。
builder.Services.Configure<KnowledgeHealthOptions>(
    builder.Configuration.GetSection(KnowledgeHealthOptions.SectionName));
builder.Services.AddScoped<KnowledgeHealthCollector>();
// 🔴 [[IADR-0299]] 決定 3: 単一書き手化。受け口は**全量スナップショット置換**であり、2 レプリカが
// 同時に走ると片方の DELETE が他方の INSERT 済み行を消して**恒久的に過少な件数**が残る。
// steady state は replicas: 1 だが、ローリング更新の maxSurge で新旧 2 pod が同時に生きる。
// IsRelational はプロバイダ判定のみで DB 接続しないため、起動時にスコープを張って安全に評価できる。
builder.Services.AddSingleton<IKnowledgeHealthLeaseCoordinator>(sp =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
    if (!db.Database.IsRelational())
        return new NoOpKnowledgeHealthLeaseCoordinator();
    return new PostgresKnowledgeHealthLeaseCoordinator(
        connStr, sp.GetRequiredService<ILogger<PostgresKnowledgeHealthLeaseCoordinator>>());
});
builder.Services.AddHostedService<KnowledgeHealthHostedService>();

// FR-14, ADR-0018 / #1016: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// 🔴 ADR-0027, ADR-0057 / #1016: DocumentDeleted を購読し、グラフ（ノード・辺・AI 提案）から
// 当該文書の痕跡を掃除する。ADR-0033 決定 2 / #911: DocumentUpdated を購読し、ABAC 属性を
// デノーマライズ保持する（graph-sync 段）。本サービス初のメッセージング導入であり、
// 最初から Wolverine である（MassTransit は選べない —— backend-library-baseline 非掲載のため
// 新規参照は即 fail。ADR-0030）。
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
    opts.ServiceName = "graph-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var deleteStep = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);
    var syncStep = opts.AddPlatformWolverineStep<GraphDocumentSyncConsumer>(pipeline);

    var graphDeleteQueue = deleteStep?.Queue ?? nameof(DocumentDeleted);
    var graphSyncQueue = syncStep?.Queue ?? nameof(DocumentUpdated);

    // 手順 3（購読側の束ね）/ #992: 各キューをイベント型名の fan-out exchange へ束ねる。
    // **キュー名を分けるだけでは何も届かない** —— 束ねて初めて発行が届く。
    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision()
        .BindPlatformQueue<DocumentDeleted>("graph-service", graphDeleteQueue)
        .BindPlatformQueue<DocumentUpdated>("graph-service", graphSyncQueue);

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う
    // （fan-out の保存: 他購読サービスと別キューになりサービス名前置で分かれる）。
    // ハンドラへの振り分けはメッセージ型で決まる（キュー 2 本 → 同一ホスト内で型別ディスパッチ）。
    opts.ListenToPlatformQueue("graph-service", graphDeleteQueue);
    opts.ListenToPlatformQueue("graph-service", graphSyncQueue);

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
// FR-16, ADR-0024 §2: MCP ツール定義の自己申告（メッシュ内部限定。#1020）。
app.MapMcpToolEndpoints();

app.Run();

public partial class Program { }
