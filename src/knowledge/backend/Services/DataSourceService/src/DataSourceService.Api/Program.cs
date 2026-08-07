using DataSourceService.Api.Composable.Adapters;
using DataSourceService.Api.Foundation.Endpoints;
using DataSourceService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Ports;
using DataSourceService.Api.Foundation.Services;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

const string ServiceName = "microservices-platform.datasource-service";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, logConfig) =>
    logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp",
        tags: ["ready"]);
// #269: ブローカ疎通の readiness は MassTransit 組み込みの "masstransit-bus"（tag "ready"）で満たす。
// 外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-01: DataSource DbContext
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=datasource_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<DataSourceDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit
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
builder.Services.AddPlatformIntrospection("datasource-service", new PipelineOptions());

// FR-01, UC-04, IADR-0051: 実データソースコネクタと同期基盤。
// オブジェクトストレージ（原本格納。未設定時は Null クライアントで縮退）。
builder.Services.AddPlatformObjectStorage(builder.Configuration);
// コネクタ。新規ソースは IDataSourceConnector を追加登録するだけで対応する（プラグイン方式）。
// HTTP コネクタが使う名前付きクライアント（将来のタイムアウト/リトライ等の付与点を明示する）。
builder.Services.AddHttpClient("WikiConnector");
builder.Services.AddHttpClient("SaaSConnector");
// 業務DB コネクタの接続生成（第一プロバイダ=PostgreSQL/Npgsql。IADR-0055）。
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IDataSourceConnector, FileSystemConnector>();  // 優先1: filesystem
builder.Services.AddSingleton<IDataSourceConnector, WikiConnector>();        // 優先2: Wiki（IADR-0053）
builder.Services.AddSingleton<IDataSourceConnector, SaaSConnector>();        // 優先3: SaaS（IADR-0054）
builder.Services.AddSingleton<IDataSourceConnector, DatabaseConnector>();    // 優先4: 業務DB（IADR-0055）
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<SyncFailureTracker>();
// SC-06（planning#200 / 裁定 Q15）, IADR-0136: 「次回同期」は共通間隔の次回実行時刻（全ソース同値）である。
// ワーカーが起動時に位相を記録し、/datasources が読む。時計は BCL の TimeProvider（テストで固定できる）。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyncSchedule>();
builder.Services.AddScoped<DataSourceSyncService>();
// 定期同期（既定無効。DataSourceSync:Enabled=true で有効化）。
builder.Services.Configure<DataSourceSyncOptions>(
    builder.Configuration.GetSection(DataSourceSyncOptions.SectionName));
// IADR-0083 (#305): 定期同期の単一書き手化（本番マルチレプリカでの冗長 fetch 排除）。リレーショナル（Npgsql）は
// advisory lock で排他し、非リレーショナル（InMemory 等）は NoOp で従来どおり毎サイクル実行する（後方互換）。
// IsRelational はプロバイダ判定のみで DB 接続しないため、起動時にスコープを張って安全に評価できる。
builder.Services.AddSingleton<ISyncLeaseCoordinator>(sp =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
    if (!db.Database.IsRelational())
        return new NoOpSyncLeaseCoordinator();
    return new PostgresAdvisoryLockLeaseCoordinator(
        connStr, sp.GetRequiredService<ILogger<PostgresAdvisoryLockLeaseCoordinator>>());
});
builder.Services.AddHostedService<DataSourceSyncHostedService>();

var app = builder.Build();

// FR-01: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapDataSourceEndpoints();

app.Run();

public partial class Program { }
