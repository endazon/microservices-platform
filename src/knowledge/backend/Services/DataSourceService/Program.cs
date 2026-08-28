using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Features.DataSources;
using DataSourceService.Infrastructure.Persistence;
using DataSourceService.Domain.Ports;
using DataSourceService.Domain;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.RabbitMQ;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "microservices-platform.datasource-service";

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
    // ADR-0027 / #441 E1 / W4: MassTransit を外すと "masstransit-bus" の readiness も消える。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
// #269 / #441 E1: ブローカ疎通の readiness。**MassTransit 撤去に伴い "masstransit-bus" は消えた**ため、
// W4 の AddPlatformWolverineBroker() が肩代わりする（上の health checks へ配線済み）。
// 外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-01: DataSource DbContext
builder.Services.AddDbContext<DataSourceDbContext>(opt => opt.UseNpgsql(connStr));

// ADR-0027 / #441 E1: メッセージング基盤は Wolverine。**本サービスは発行のみで購読を持たない。**
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
    opts.ServiceName = "datasource-service";

    // 段をホストしないので探索は要らない。規約探索を切って明示配線に寄せる。
    opts.Discovery.DisableConventionalDiscovery();

    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
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
