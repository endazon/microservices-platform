using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WikiService.Api.Composable.Steps;
using WikiService.Api.Foundation.Endpoints;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;
using WikiService.Api.Composable.Adapters;

const string ServiceName = "knowledge-platform.wiki-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=wiki_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
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

// ADR-0003: MassTransit — DocumentUpdated を購読し Wiki ページに同期
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（wiki-sync / wiki-delete）の実効値を申告する。
builder.Services.AddPlatformIntrospection("wiki-service", pipeline, i => i
    .AddStep<DocumentSyncConsumer>()
    .AddStep<DocumentDeletedConsumer>());

builder.Services.AddMassTransit(x =>
{
    x.AddPlatformPipelineStep<DocumentSyncConsumer>(pipeline);
    // Issue #88: 文書削除の伝播（Wiki.js 実体撤去・メタデータ削除）。
    x.AddPlatformPipelineStep<DocumentDeletedConsumer>(pipeline);
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // ADR-0003: DocumentUpdated 購読による Wiki 同期（DocumentSyncConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UsePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
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
