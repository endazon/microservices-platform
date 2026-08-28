using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Knowledge.Contracts.Events;
using WikiService.Features.Wiki;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.ExternalServices;

const string ServiceName = "microservices-platform.wiki-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR, #1012: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / E3a: Wolverine 購読側（wiki-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
// #269: ブローカ疎通の readiness は上の AddPlatformWolverineBroker()（W4）が満たす
// （E3b で MassTransit を撤去した）。外部 AspNetCore.HealthChecks.Rabbitmq は
// RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-13: Wiki DbContext
builder.Services.AddDbContext<WikiDbContext>(opt => opt.UseNpgsql(connStr));

// FR-13, FR-05, ADR-0011: 閲覧の ABAC 判定は本システム（AuthorizationService）が担う。
builder.Services.AddHttpClient("AuthorizationService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:5005"));
builder.Services.AddScoped<IWikiAccessResolver, WikiAccessResolver>();

// FR-13, UC-07, ADR-0011, IADR-0021: Wiki.js への同期・本文取得（GraphQL API push）。
// API キーは環境変数/シークレット経由で注入（コミットしない）。
builder.Services.AddHttpClient<IWikiJsClient, WikiJsGraphQlClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["WikiJs:GraphQlEndpoint"]
        ?? "http://wiki-js:3000/graphql");
    var apiKey = builder.Configuration["WikiJs:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey))
        c.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
});
// FR-06, ADR-0014/ADR-0015: オブジェクトストレージ（MinIO）クライアント（storage:// 本文の実取得用）。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

// IADR-0021: 正規化 Markdown 本文を MarkdownUri から取得して Wiki.js へ push する
// （storage:// はオブジェクトストレージから実取得。IADR-0020 ゲートウェイ経由の ABAC 強制と整合）。
builder.Services.AddHttpClient<IWikiContentReader, StorageMarkdownReader>();

// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（wiki-sync / wiki-delete）の実効値を申告する。
// 両段とも Wolverine 段（E3a / E3b）なので AddWolverineStep で申告する（IADR-0239 と同じ方針:
// IPipelineStep<TIn> から入力型を導出し、導出できなければ起動失敗）。
builder.Services.AddPlatformIntrospection("wiki-service", pipeline, i => i
    .AddWolverineStep<DocumentSyncConsumer>()
    .AddWolverineStep<DocumentDeletedConsumer>());

var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? "amqp://guest:guest@rabbitmq:5672";

// 🔴 ADR-0027 / E3a・E3b: **wiki-delete 段（DocumentDeleted 購読）と wiki-sync 段
// （DocumentUpdated 購読）はともに Wolverine である。MassTransit は本サービスから撤去した**
// （E3b で最後の MT 購読が移った。バックエンドライブラリ baseline の行も削除済み）。
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "wiki-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var deleteStep = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);
    var syncStep = opts.AddPlatformWolverineStep<DocumentSyncConsumer>(pipeline);

    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う。
    // fan-out の保存: DocumentUpdated は ingestion-service と別キューになる（サービス名前置）。
    // ハンドラへの振り分けはメッセージ型で決まる（キュー 2 本 → 同一ホスト内で型別ディスパッチ）。
    opts.ListenToPlatformQueue("wiki-service", deleteStep?.Queue ?? nameof(DocumentDeleted));
    opts.ListenToPlatformQueue("wiki-service", syncStep?.Queue ?? nameof(DocumentUpdated));

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

var app = builder.Build();

// FR-13: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapWikiEndpoints();

app.Run();

public partial class Program { }
