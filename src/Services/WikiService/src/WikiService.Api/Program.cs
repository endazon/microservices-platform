using KnowledgePlatform.Shared.Infrastructure.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WikiService.Api.Consumers;
using WikiService.Api.Endpoints;
using WikiService.Api.Infrastructure;
using WikiService.Api.Services;

const string ServiceName = "knowledge-platform.wiki-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
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
// IADR-0021: 正規化 Markdown 本文を MarkdownUri から取得して Wiki.js へ push する。
builder.Services.AddHttpClient<IWikiContentReader, StorageMarkdownReader>();

// ADR-0003: MassTransit — DocumentUpdated を購読し Wiki ページに同期
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DocumentSyncConsumer>();
    // Issue #88: 文書削除の伝播（Wiki.js 実体撤去・メタデータ削除）。
    x.AddConsumer<DocumentDeletedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // ADR-0003: DocumentUpdated 購読による Wiki 同期（DocumentSyncConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UseKnowledgePlatformRetry();

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

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapOpenApi();

app.MapWikiEndpoints();

app.Run();

public partial class Program { }
