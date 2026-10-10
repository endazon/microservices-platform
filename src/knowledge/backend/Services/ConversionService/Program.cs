using Wolverine;
using Wolverine.RabbitMQ;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Grpc;
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
using Microsoft.Extensions.DependencyInjection.Extensions;

const string ServiceName = "microservices-platform.conversion-service";

// FR-15, IADR-0029: 自己申告エンドポイントの最小 HTTP サーフェスのため WebApplication を用いる。
// MassTransit コンシューマ（変換ワーカー）は従来どおり IHostedService として稼働する。
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// NFR-09, FR-12, SC-07, ADR-0109 決定 3, ADR-0084 決定 1, IADR-0465 (#1520): **BFF が中継した利用者の資格情報を
// 自ら検証する**（他の後段サービスと同じ `AddPlatformAuth`。Keycloak の JWT を `Auth:Authority` の metadata で検証）。
// 🔴 **これだけでは端点は 1 つも閉じない**（`FallbackPolicy` は置かない）—— 門は `/jobs` の群と各操作が持つ
// （`ConversionJobEndpoints`）。
builder.Services.AddPlatformAuth(builder.Configuration);
// FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0462]] フォローアップ 3 (#1537, #1514, #1255 経路 ⑤):
// east-west gRPC の h2c リスナ（`Grpc:Port`。未設定なら立てない）。面は自己申告の gRPC 面
// （`MapPlatformIntrospection` が REST と対で張る。構成情報 API が宛先ごと opt-in で収集する）。
// 面の `ServiceCaller` は上の `AddPlatformAuth`（[[IADR-0465]]・#1520）が判定する —— それが着地したので配線できる。
// HTTP/1.1 のポート（REST・introspection）はそのまま残る。
builder.AddPlatformGrpcListener();

// FR-12, UC-06, SC-07, IADR-0043: 変換ジョブ読み取りモデルの Postgres+EF 永続化。
// ADR-0002: ConversionService 専用 DB（conversion_svc）。起動時に MigrateAsync でスキーマ最新化。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
            "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
            + "ConnectionStrings__DefaultConnection で注入する）。");
builder.Services.AddDbContext<ConversionJobDbContext>(opt => opt.UseNpgsql(connStr));

// FR-12, UC-06, ADR-0012, IADR-0320 (#1097): 本文変換の構成（縮退の可否）。**既定は fail-closed**。
builder.Services.Configure<ConversionOptions>(
    builder.Configuration.GetSection(ConversionOptions.SectionName));
var allowDegradedConversion = builder.Configuration
    .GetSection(ConversionOptions.SectionName).Get<ConversionOptions>()?.AllowDegradedBodyConversion
    ?? false;

// DB 到達性の readiness ヘルスチェック（DataSourceService 準拠）。
var health = builder.Services.AddPlatformHealthChecks()
    .AddPlatformWolverineBroker()
    .AddNpgSql(connStr, tags: ["ready"]);

// FR-12, IADR-0320 決定 5 (#1097): 🔴 **pandoc が実行時イメージに在ることを readiness で確かめる。**
// 従前 pandoc の欠落はどこにも現れなかった（変換は縮退して「成功」し、probe も緑だった）。
// 縮退を許した開発機では登録しない —— そこでは縮退が正常な振る舞いである。
if (!allowDegradedConversion)
{
    health.AddCheck<PandocHealthCheck>("pandoc", tags: ["ready"]);
    // FR-12, ADR-0070 決定 2, IADR-0356 決定 7 (#1192): PDF のテキスト層抽出器（pdftotext）も同じ線で readiness に載せる。
    health.AddCheck<PdfToTextHealthCheck>("pdftotext", tags: ["ready"]);
}

// FR-12, ADR-0012, ADR-0070 決定 2, IADR-0356 決定 2 (#1192): 本文変換。
// `IBodyConverter` は形式で振り分ける合成器であり、PDF はテキスト層の抽出器（pdftotext）、
// それ以外は pandoc が変換する。`NormalizationService` は合成器しか知らない（IADR-0008 の 3 ポートは不変）。
builder.Services.AddSingleton<PandocConversionService>();
builder.Services.AddSingleton<PdfTextLayerConverter>();
builder.Services.AddSingleton<IBodyConverter, FormatRoutingBodyConverter>();

// FR-12, ADR-0014/ADR-0015（Superseded by ADR-0106）, IADR-0024: 正規化本文・資産の S3 互換オブジェクトストレージ（SeaweedFS）保管。
// 共有クライアントを登録し、起動時にバケット存在・バージョニングを保証する。
builder.Services.AddPlatformObjectStorage(builder.Configuration);
builder.Services.AddPlatformObjectStorageBootstrap();
builder.Services.AddSingleton<IObjectStore, StorageObjectStore>();

// FR-12, ADR-0012/0010: 図のコード化（LLMゲートウェイ経由、機密区分で送信制御）。
//
// FR-12, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0400, [[IADR-0533]] (#1255): 図のコード化の輸送は east-west gRPC だけである
// （［2026-10-10］REST の `LlmGatewayDiagramCoder` は撤去した）。`Services:LlmGatewayGrpc` が構成されていなければ
// 生成クライアントは常に `UNAVAILABLE` を受け取り、図は画像として保持される（理由 `llm-call-failed`）。
//
// UC-06, IADR-0008（2026-09-27 追記 / #1621）: **図のコード化の時間の上限**（1 回の期限・1 文書の総枠・受け口の実行期限）。
// 受け口の期限が「総枠＋1 回の期限」を超えていなければ、ここで起動を止める（`DiagramCodingLimits.From`）。
var diagramCodingLimits = DiagramCodingLimits.From(builder.Configuration);
builder.Services.AddSingleton(diagramCodingLimits);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddLlmGatewayGrpcClient(builder.Configuration);
builder.Services.AddSingleton<IDiagramCoder, LlmGatewayGrpcDiagramCoder>();

// FR-12, UC-06: 正規化オーケストレータ（本文＋図＋保管を束ねる）。
builder.Services.AddScoped<INormalizationService, NormalizationService>();

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル（状況・失敗一覧・人手補正）。
// EF（Postgres）実装。DbContext が scoped のため本ストアも scoped（メッセージ消費ごとの DI スコープで解決）。
builder.Services.AddScoped<IConversionJobStore, EfConversionJobStore>();

// FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1（図のコード化のやり直し）。
// 本文の図ブロックを置換して DocumentNormalized を再発行する（再変換ではない）。
builder.Services.AddScoped<IFigureCorrectionService, FigureCorrectionService>();

// UC-06, SC-07, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0393:
// 人手補正の入力検証。**アセンブリ走査（AddValidatorsFromAssembly）は使わない** —— 登録が暗黙になり、
// 検証器を消しても起動時には何も起きず、端点が黙って無検証になるためである。
// 1 行 1 検証器の明示登録なら、消したときにコンパイルか DI 解決で止まる。
builder.Services.AddScoped<IValidator<FigureCorrectionRequest>, FigureCorrectionValidator>();

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

    // 手順 3（購読側の束ね）/ #992: 自分のキューをイベント型名の fan-out exchange へ束ねる。
    // **キュー名を分けるだけでは何も届かない** —— 束ねて初めて発行が届く。
    // FR-14 / #1801: 段宣言（step）を渡す。queue 宣言があればそれ、無ければイベント型名で束ね・購読し、
    // `enabled: false` の段ではキューを束ねずリスナーも立てない（WolverineExtensions の段宣言版）。
    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision()
        .BindPlatformQueue<RawDocumentFetched>("conversion-service", step);

    // ADR-0027 手順 3（発行側）/ #992 / [[IADR-0314]]: **外向きの経路を宣言する。**
    // これが無いと `No routes can be determined for Envelope ...` を info ログへ 1 行出して
    // 黙って捨てられる（例外もヘルスチェックの赤も出ない。稼働 k3s で実測）。
    // 再試行（/retry）が RawDocumentFetched を再発行するため、購読側でもあり発行側でもある。
    // 段が `enabled: false` でも発行の経路は残す（再試行の発行は段の有無と独立）。
    opts.RoutePlatformEvent<RawDocumentFetched>();

    // 手順 3 の適用点。queue 宣言があればそれを、無ければイベント型名を使う（無効の段では立てない）。
    opts.ListenToPlatformQueue<RawDocumentFetched>("conversion-service", step);

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();

    // UC-06, IADR-0008（2026-09-27 追記 / #1621）: 受け口の実行期限を明示する（Wolverine の既定 60 秒のままにしない）。
    // 図のコード化の総枠と 1 回の期限が収まる長さである（`DiagramCodingLimits`）。
    opts.Policies.Add(new RawDocumentFetchedTimeoutPolicy(diagramCodingLimits.HandlerTimeout));
});

var app = builder.Build();

// IADR-0043: 起動時にスキーマを最新 Migration へ更新（DataSourceService 準拠）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConversionJobDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

// NFR-09, ADR-0109 決定 3, IADR-0465 (#1520): 認証・認可のミドルウェア（相関 ID を含む。他の後段サービスと同じ）。
// 端点の登録より前に置く。ヘルスチェックと自己申告は門を持たないので、ここを通っても開いたままである。
app.UsePlatformMiddleware();

// DB 到達性の readiness ヘルスチェック（/health/ready・/health/live）。
app.MapPlatformHealthChecks();

// FR-15, IADR-0029: 自己申告（gRPC 面 `ServiceIntrospection/Get`。［2026-10-10 / #1517］REST の GET /internal/introspection は撤去した）。
// メッシュ内部限定（ingress へ公開しない。IADR-0017 ネットワーク分離 / IADR-0026 mTLS が防御）。
app.MapPlatformIntrospection();

// FR-12, UC-06, SC-07: 変換ジョブの状況照会・人手補正（BFF 経由でのみ到達）。
// NFR-09, ADR-0109 決定 3, IADR-0465 (#1520): 5 口すべてが利用者の資格情報で門を判定する。
app.MapConversionJobEndpoints();

app.Run();

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
