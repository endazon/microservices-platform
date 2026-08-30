using Wolverine;
using Wolverine.RabbitMQ;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using ConversionService.Features.ConversionJobs;
using ConversionService.Features.ConversionJobs.CorrectFigure;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Infrastructure.Messaging;
using ConversionService.Infrastructure.ExternalServices;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "microservices-platform.conversion-service";

// FR-15, IADR-0029: 自己申告エンドポイントの最小 HTTP サーフェスのため WebApplication を用いる。
// MassTransit コンシューマ（変換ワーカー）は従来どおり IHostedService として稼働する。
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// FR-12, UC-06, SC-07, IADR-0043: 変換ジョブ読み取りモデルの Postgres+EF 永続化。
// ADR-0002: ConversionService 専用 DB（conversion_svc）。起動時に MigrateAsync でスキーマ最新化。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
            "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
            + "ConnectionStrings__DefaultConnection で注入する）。");
builder.Services.AddDbContext<ConversionJobDbContext>(opt => opt.UseNpgsql(connStr));

// FR-12, UC-06, ADR-0012, IADR-0318 (#1097): 本文変換の構成（縮退の可否）。**既定は fail-closed**。
builder.Services.Configure<ConversionOptions>(
    builder.Configuration.GetSection(ConversionOptions.SectionName));
var allowDegradedConversion = builder.Configuration
    .GetSection(ConversionOptions.SectionName).Get<ConversionOptions>()?.AllowDegradedBodyConversion
    ?? false;

// DB 到達性の readiness ヘルスチェック（DataSourceService 準拠）。
var health = builder.Services.AddPlatformHealthChecks()
    .AddPlatformWolverineBroker()
    .AddNpgSql(connStr, tags: ["ready"]);

// FR-12, IADR-0318 決定 5 (#1097): 🔴 **pandoc が実行時イメージに在ることを readiness で確かめる。**
// 従前 pandoc の欠落はどこにも現れなかった（変換は縮退して「成功」し、probe も緑だった）。
// 縮退を許した開発機では登録しない —— そこでは縮退が正常な振る舞いである。
if (!allowDegradedConversion)
    health.AddCheck<PandocHealthCheck>("pandoc", tags: ["ready"]);

// FR-12, ADR-0012: 本文変換（pandoc ラッパー）。
builder.Services.AddSingleton<IBodyConverter, PandocConversionService>();

// FR-12, ADR-0014/ADR-0015, IADR-0024: 正規化本文・資産の S3 互換オブジェクトストレージ（MinIO）保管。
// 共有クライアントを登録し、起動時にバケット存在・バージョニングを保証する。
builder.Services.AddPlatformObjectStorage(builder.Configuration);
builder.Services.AddPlatformObjectStorageBootstrap();
builder.Services.AddSingleton<IObjectStore, StorageObjectStore>();

// FR-12, ADR-0012/0010: 図のコード化（LLMゲートウェイ経由、機密区分で送信制御）。
builder.Services.AddHttpClient<IDiagramCoder, LlmGatewayDiagramCoder>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:5007"));

// FR-12, UC-06: 正規化オーケストレータ（本文＋図＋保管を束ねる）。
builder.Services.AddScoped<INormalizationService, NormalizationService>();

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル（状況・失敗一覧・人手補正）。
// EF（Postgres）実装。DbContext が scoped のため本ストアも scoped（メッセージ消費ごとの DI スコープで解決）。
builder.Services.AddScoped<IConversionJobStore, EfConversionJobStore>();

// FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1（図のコード化のやり直し）。
// 本文の図ブロックを置換して DocumentNormalized を再発行する（再変換ではない）。
builder.Services.AddScoped<IFigureCorrectionService, FigureCorrectionService>();

// FR-12 / #441 E1: DocumentNormalized の発行は MassTransit のまま（辺は E2 の射程）。
// 🔴 **別ファイルへ切り出してある** —— 同一ファイルに両トランスポートの using が同居すると、
// トポロジ検査の発行側 union に wolverine が混ざり、E2 で違反が報告されなくなる。
builder.Services.AddScoped<IDocumentNormalizedPublisher, MassTransitDocumentNormalizedPublisher>();

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018, IADR-0029: 自己申告（イントロスペクション）— この段（convert）の実効値を申告する。
// これによりドリフト検出でワーカー段が Verifiable となり、適用漏れ（MissingApply）を検出できる。
builder.Services.AddPlatformIntrospection("conversion-service", pipeline,
    i => i.AddWolverineStep<RawDocumentFetchedConsumer>());

// 🔴 ADR-0027 / #441 E1: **購読は Wolverine へ移した。発行は MassTransit のままである。**
// DocumentNormalized の辺は E2 の射程であり、辺は原子的に動かす（IADR-0234 決定 3）ため
// 本 PR では触らない。したがって本サービスは移行期間中 **両スタックを同居させる**。
// NFR, ADR-0027, #1022: ブローカ接続。**既定資格情報をイメージへ焼かない** —— appsettings.json からも
// 撤去したため、構成が注入されていなければここで落ちる（注入漏れが「既定の資格情報で接続成功」へ
// 倒れない。#1012 / IADR-0286 の DB と同型。IADR-0291）。**1 サービス 1 解決点にする。**
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（k8s は helm の global.messaging、compose は x-rabbit-env が注入する）。"
        + " 既定値は持たない —— 未注入をブローカへの接続失敗として現れさせないためである。");

// 発行側（DocumentNormalized）だけが残る MassTransit。段の登録はもう行わない。
builder.Services.AddMassTransit(x =>
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitConnection);
        cfg.UsePlatformRetry();
        cfg.ConfigureEndpoints(ctx);
    }));

// 購読側（RawDocumentFetched）は Wolverine。
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "conversion-service";

    // 宣言との突合は共通ヘルパが行う（未宣言・consumer 不一致・input 不一致は起動失敗）。
    // 戻り値の段宣言を受けるのは、queue 上書きを黙って無視しないためである（IADR-0239 決定 4）。
    var step = opts.AddPlatformWolverineStep<RawDocumentFetchedConsumer>(pipeline);

    var conversionQueue = step?.Queue ?? nameof(RawDocumentFetched);

    // 手順 3（購読側の束ね）/ #992: 自分のキューをイベント型名の fan-out exchange へ束ねる。
    // **キュー名を分けるだけでは何も届かない** —— 束ねて初めて発行が届く。
    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision()
        .BindPlatformQueue<RawDocumentFetched>("conversion-service", conversionQueue);

    // ADR-0027 手順 3（発行側）/ #992 / [[IADR-0314]]: **外向きの経路を宣言する。**
    // これが無いと `No routes can be determined for Envelope ...` を info ログへ 1 行出して
    // 黙って捨てられる（例外もヘルスチェックの赤も出ない。稼働 k3s で実測）。
    // 再試行（/retry）が RawDocumentFetched を再発行するため、購読側でもあり発行側でもある。
    opts.RoutePlatformEvent<RawDocumentFetched>();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う。
    opts.ListenToPlatformQueue("conversion-service", conversionQueue);

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

var app = builder.Build();

// IADR-0043: 起動時にスキーマを最新 Migration へ更新（DataSourceService 準拠）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// DB 到達性の readiness ヘルスチェック（/health/ready・/health/live）。
app.MapPlatformHealthChecks();

// FR-15, IADR-0029: 自己申告エンドポイント（GET /internal/introspection）。
// メッシュ内部限定（ingress へ公開しない。IADR-0017 ネットワーク分離 / IADR-0026 mTLS が防御）。
app.MapPlatformIntrospection();

// FR-12, UC-06, SC-07: 変換ジョブの状況照会・人手補正（BFF 経由でのみ到達）。
app.MapConversionJobEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
