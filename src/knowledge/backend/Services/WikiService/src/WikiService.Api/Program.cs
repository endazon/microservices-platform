using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Knowledge.Contracts.Events;
using WikiService.Api.Composable.Steps;
using WikiService.Api.Foundation.Endpoints;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;
using WikiService.Api.Composable.Adapters;

const string ServiceName = "microservices-platform.wiki-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / E3a: Wolverine 購読側（wiki-delete 段）のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp",
        tags: ["ready"]);
// #269: MassTransit 側（wiki-sync 段が残る間）のブローカ疎通は MassTransit 組み込みの
// "masstransit-bus"（tag "ready"）で満たす。
// 外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-13: Wiki DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp";
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

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit — DocumentUpdated を購読し Wiki ページに同期
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（wiki-sync / wiki-delete）の実効値を申告する。
// wiki-delete は Wolverine 段（E3a）なので AddWolverineStep で申告する（IADR-0239 と同じ方針:
// IPipelineStep<TIn> から入力型を導出し、導出できなければ起動失敗）。
builder.Services.AddPlatformIntrospection("wiki-service", pipeline, i => i
    .AddStep<DocumentSyncConsumer>()
    .AddWolverineStep<DocumentDeletedConsumer>());

var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? "amqp://guest:guest@rabbitmq:5672";

// 🔴 ADR-0027 / E3a: **wiki-delete 段（DocumentDeleted 購読）は Wolverine へ移した。**
// wiki-sync 段（DocumentUpdated 購読）は辺 E3b の射程であり、辺は原子的に動かす（IADR-0234 決定 3）
// ため本段では MassTransit のまま。移行期間中 **両スタックを同居させる**（E1 の ConversionService と同じ形）。
builder.Services.AddMassTransit(x =>
{
    x.AddPlatformPipelineStep<DocumentSyncConsumer>(pipeline);
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitConnection);

        // ADR-0003（Superseded by ADR-0027・注記は #580）: DocumentUpdated 購読による Wiki 同期（DocumentSyncConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UsePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
});

// Issue #88 / E3a: 文書削除の伝播（Wiki.js 実体撤去・メタデータ削除）— Wolverine 購読。
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "wiki-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var step = opts.AddPlatformWolverineStep<DocumentDeletedConsumer>(pipeline);

    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う。
    opts.ListenToPlatformQueue("wiki-service", step?.Queue ?? nameof(DocumentDeleted));

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
