using DataSourceService.Api.Composable.Adapters;
using DataSourceService.Api.Foundation.Endpoints;
using DataSourceService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Ports;
using DataSourceService.Api.Foundation.Services;
using KnowledgePlatform.Shared.Infrastructure.Composable.Adapters.Storage;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "knowledge-platform.datasource-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigureKnowledgePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddKnowledgePlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddKnowledgePlatformAuth(builder.Configuration);
builder.Services.AddKnowledgePlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp",
        tags: ["ready"])
    .AddRabbitMQ(
        rabbitConnectionString: builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-01: DataSource DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<DataSourceDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0003: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");
        cfg.ConfigureEndpoints(ctx);
    });
});

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。パイプライン段はホストせず、
// データソースコネクタは実行時データ（DB）のため静的申告の対象外。到達可能性とトポロジを与えるため存在申告する。
builder.Services.AddKnowledgePlatformIntrospection("datasource-service", new PipelineOptions());

// FR-01, UC-04, IADR-0051: 実データソースコネクタと同期基盤。
// オブジェクトストレージ（原本格納。未設定時は Null クライアントで縮退）。
builder.Services.AddKnowledgePlatformObjectStorage(builder.Configuration);
// コネクタ。新規ソースは IDataSourceConnector を追加登録するだけで対応する（プラグイン方式）。
// WikiConnector が使う名前付きクライアント（将来のタイムアウト/リトライ等の付与点を明示する）。
builder.Services.AddHttpClient("WikiConnector");
builder.Services.AddSingleton<IDataSourceConnector, FileSystemConnector>();  // 優先1: filesystem
builder.Services.AddSingleton<IDataSourceConnector, WikiConnector>();        // 優先2: Wiki（IADR-0053）
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<SyncFailureTracker>();
builder.Services.AddScoped<DataSourceSyncService>();
// 定期同期（既定無効。DataSourceSync:Enabled=true で有効化）。
builder.Services.Configure<DataSourceSyncOptions>(
    builder.Configuration.GetSection(DataSourceSyncOptions.SectionName));
builder.Services.AddHostedService<DataSourceSyncHostedService>();

var app = builder.Build();

// FR-01: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseKnowledgePlatformMiddleware();
app.MapKnowledgePlatformHealthChecks();
app.MapKnowledgePlatformIntrospection();
app.MapOpenApi();

app.MapDataSourceEndpoints();

app.Run();

public partial class Program { }
