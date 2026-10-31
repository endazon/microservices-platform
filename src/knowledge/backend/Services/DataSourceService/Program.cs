using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Features.DataSources;
using DataSourceService.Features.DataSources.Introspection;
using DataSourceService.Features.DataSources.Sync;
using DataSourceService.Features.DataSources.Update;
using FluentValidation;
using DataSourceService.Infrastructure.Persistence;
using DataSourceService.Infrastructure.Secrets;
using DataSourceService.Domain.Ports;
using DataSourceService.Domain;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.RabbitMQ;
using Microsoft.EntityFrameworkCore;
using Knowledge.Contracts.Events;

const string ServiceName = "microservices-platform.datasource-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
// FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0462]] (#1514, #1255 経路 ⑤):
// east-west gRPC の h2c リスナ（`Grpc:Port`。未設定なら立てない）。面は自己申告の gRPC 面
// （`MapPlatformIntrospection` が REST と対で張る。構成情報 API が宛先ごと opt-in で収集する）。
// HTTP/1.1 のポート（REST・/health/*・introspection）はそのまま残り、readiness も 8080 のままである。
builder.AddPlatformGrpcListener();
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

    opts.UsePlatformRabbitMq(rabbitConnection).AutoProvision();

    // ADR-0027 手順 3（発行側）/ #992 / [[IADR-0314]]: **外向きの経路を宣言する。**
    // これが無いと `No routes can be determined for Envelope ...` を info ログへ 1 行出して
    // 黙って捨てられる（例外もヘルスチェックの赤も出ない。稼働 k3s で実測）。
    opts.RoutePlatformEvent<RawDocumentFetched>();

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();

    // FR-01, UC-04, ADR-0027 §決定, IADR-0604 決定 1, IADR-0609 (#2169), IADR-0657 (#2361): 発行の**耐久化**（Outbox の表への永続化）。
    // 同期は `RawDocumentFetched` を 1 件ごとの短いトランザクションで送出表へコミットしてから次へ進む
    // （`WolverineRawDocumentFetchedPublisher`。表へ書けなければその 1 件は失敗になり、watermark は進まない）。
    // watermark の保存とは同じトランザクションに載せない（逆向きの食い違い〔発行済み・未保存〕は決定的な DocumentId による冪等な再発行で収束する）。
    // 🔴 **接続文字列は DbContext と同じもの**（サービスごとの DB に `wolverine` スキーマを置く）。表は起動時に Wolverine が作る。
    opts.UsePlatformTransactionalOutbox(connStr);
});

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。パイプライン段はホストしない。
// ［2026-10-26 / #2118・IADR-0029 フォローアップ 2］**登録済みのコネクタのプラグイン**（下の IDataSourceConnector の 4 種）を
// 有効として申告する（計画 10_composability §5「コネクタ一覧」の**狭い読み**）。~~🔴 「有効・無効状態」はこの読みでは常に有効で
// 自明である —— 計画の意図は SC-06 に登録したデータソース（DB の実行時データ）の集約かもしれない。その読みは申告を要求ごとに
// 組み立てる仕組みが要るので別に扱い、計画の答えを待つ。~~ ［2026-10-31 / IADR-0666］計画は広い読みを採った。下の寄与を参照。
// 種別名は各コネクタの定数（Type）1 か所から取り、DI の登録との一致を試験が固定する。
// 選択中のオブジェクトストレージ（object-storage）も申告する。
builder.Services.AddPlatformIntrospection("datasource-service", new PipelineOptions(), i => i
    .AddConnector(FileSystemConnector.Type, enabled: true)
    .AddConnector(WikiConnector.Type, enabled: true)
    .AddConnector(SaaSConnector.Type, enabled: true)
    .AddConnector(DatabaseConnector.Type, enabled: true)
    .AddObjectStoragePort(builder.Configuration));
// FR-15, FR-01, SC-06, IADR-0666 (#2285 の (b)(ii)): ［2026-10-31］計画の「コネクタ一覧」は SC-06 に登録したデータソースと有効・無効を指す
// （planning#850 の裁定 (ii)）。上のプラグイン 4 種は計画の「コネクタの種別」（別の項目・常に有効）である。データソースは要求のたびの寄与で載せる。
builder.Services.AddIntrospectionContributor<RegisteredDataSourcesIntrospectionContributor>();

// FR-05, UC-04, SC-06, SC-17, ADR-0064 決定 4, ADR-0074 決定 4 (#1194): `owner` の写像先の実在検証。
//
// FR-05, UC-04, SC-06, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4, IADR-0401 決定 2・3, [[IADR-0533]] (#1255):
// 輸送は east-west gRPC（`UserDirectory/CheckUsernames`）だけである（［2026-10-10］REST の
// `AuthorizationServiceUserDirectory`〔`/authz/users`。利用者トークンを転送〕は撤去した）。
// `Services:AuthorizationServiceGrpc` が構成されていなければ生成クライアントは常に `UNAVAILABLE` を受け取り、
// 「引けなかった」（502）へ倒れる —— 「実在しない」（400）とは混ぜない。
//
// 🔴 **gRPC 実装は利用者トークンを転送しない。** 代わりに呼び出し先の読み口を
// 「これらの名前は実在するか」へ狭めてある（`UserDirectory/CheckUsernames`）——
// 列挙も書き込みも s2s の面に無いので、SC-06 を触れない主体が名簿を引ける経路はできない。
builder.Services.AddUserDirectoryGrpcClient(builder.Configuration);
builder.Services.AddScoped<IPlatformUserDirectory,
    DataSourceService.Infrastructure.ExternalServices.GrpcPlatformUserDirectory>();

// FR-05, UC-04, SC-06, 計画 ADR-0115 決定 5, ADR-0074 決定 4, [[IADR-0472]] 決定 2 (#1557):
// 明示した部門の値域検証の輸送。**gRPC だけである**（REST の兄弟実装は作らない —— 利用者トークンの転送へ戻さない）。
// 宛先が未宣言の配備では「引けなかった」へ倒れ、明示した部門の書き込みは 502 で保存されない（黙って通さない）。
if (!string.IsNullOrWhiteSpace(builder.Configuration[AuthzScopeGrpcClient.AddressKey]))
    builder.Services.AddScoped<IDepartmentDomainDirectory,
        DataSourceService.Infrastructure.ExternalServices.GrpcDepartmentDomainDirectory>();
else
    builder.Services.AddSingleton<IDepartmentDomainDirectory,
        DataSourceService.Infrastructure.ExternalServices.UnavailableDepartmentDomainDirectory>();

// FR-01, UC-04, IADR-0051: 実データソースコネクタと同期基盤。
// オブジェクトストレージ（原本格納。未設定時は Null クライアントで縮退）。
builder.Services.AddPlatformObjectStorage(builder.Configuration);
// コネクタ。新規ソースは IDataSourceConnector を追加登録するだけで対応する（プラグイン方式）。
// HTTP コネクタが使う名前付きクライアント（将来のタイムアウト/リトライ等の付与点を明示する）。
// #1604（IADR-0083 追記）: 期限を明示する。既定 100 秒のままだと、固まった接続先 1 つが定期同期の 1 周を 100 秒止める。
// 時間切れはそのソースの失敗として数えられる（DataSourceSyncService）。
builder.Services.AddHttpClient(WikiConnector.HttpClientName, c => c.Timeout = WikiConnector.HttpTimeout);
builder.Services.AddHttpClient(SaaSConnector.HttpClientName, c => c.Timeout = SaaSConnector.HttpTimeout);
// 業務DB コネクタの接続生成（第一プロバイダ=PostgreSQL/Npgsql。IADR-0055）。
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IDataSourceConnector, FileSystemConnector>();  // 優先1: filesystem
builder.Services.AddSingleton<IDataSourceConnector, WikiConnector>();        // 優先2: Wiki（IADR-0053）
builder.Services.AddSingleton<IDataSourceConnector, SaaSConnector>();        // 優先3: SaaS（IADR-0054）
builder.Services.AddSingleton<IDataSourceConnector, DatabaseConnector>();    // 優先4: 業務DB（IADR-0055）
builder.Services.AddSingleton<ConnectorRegistry>();
// NFR-18, [[IADR-0493]] 決定 2 (#458 段 S0), [[IADR-0495]] 決定 3 (#458 段 S1): コネクタの資格情報の解決器。
// `Vault:Address` が在れば Vault 解決器（`vault:datasource/…#<key>` を Vault の KV v2 から読む。平文は移送期間の素通し）、
// 空なら S0 と同じ素通しだけ（`vault:` 参照は解決器なしとして同期を fail-closed で止める）。
// 名乗りは Pod の ServiceAccount `datasource-service`（Vault の k8s auth ロール `datasource-connector-reader`。
// policy は `secret/data/datasource/*` の read だけ。deploy/local/vault/eso/bootstrap.sh）。
builder.Services.AddConnectorSecretResolver();
// SC-06（planning#200 / 裁定 Q15）, IADR-0136: 「次回同期」は共通間隔の次回実行時刻（全ソース同値）である。
// ワーカーが起動時に位相を記録し、/datasources が読む。時計は BCL の TimeProvider（テストで固定できる）。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyncSchedule>();
// FR-01, UC-04, IADR-0657 (#2361): 原本のイベントは 1 件ごとに送出表へコミットする発行口だけで出す（`IDbContextOutbox` はスコープ単位）。
builder.Services.AddScoped<IRawDocumentFetchedPublisher, DataSourceService.Infrastructure.Messaging.WolverineRawDocumentFetchedPublisher>();
builder.Services.AddScoped<DataSourceSyncService>();

// NFR, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 端点の入力検証。**アセンブリ走査（AddValidatorsFromAssembly）は使わない** —— 登録が暗黙になり、
// 検証器を消しても起動時には何も起きず、端点が黙って無検証になる。1 行 1 検証器で明示登録する。
builder.Services.AddScoped<IValidator<UpdateDataSourceRequest>, UpdateDataSourceValidator>();
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
// NFR-18, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 2 (#458 段 S4): 移送期間の旗が偽なら、平文の行が 0 件でなければ起動しない。
// 同期のワーカーより先に登録する（ホストは登録順に開始する。平文の行のまま同期を始めない）。
builder.Services.AddHostedService<DataSourceService.Infrastructure.Secrets.PlaintextCredentialStartupGate>();
builder.Services.AddHostedService<DataSourceSyncHostedService>();

var app = builder.Build();

// FR-01: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// NFR-18, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 3・4 (#458 段 S4): 移送の入口（`migrate-connector-credentials`）。
// 稼働の Pod の中で `kubectl exec -i` で流し、終わったら終了する（ホストは開始しない＝同期のワーカー・HTTP は動かない）。
if (DataSourceService.Infrastructure.Secrets.Migration.ConnectorCredentialMigrationCommand.IsInvoked(args))
{
    Environment.ExitCode = await DataSourceService.Infrastructure.Secrets.Migration.ConnectorCredentialMigrationCommand.RunAsync(
        app.Services, args, Console.In, Console.Out, CancellationToken.None);
    return;
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapDataSourceEndpoints();

app.Run();

public partial class Program { }
