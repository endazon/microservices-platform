using DocumentService.Common.Observability;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using DocumentService.Infrastructure.Messaging;
using DocumentService.Features.Documents;
using DocumentService.Features.Documents.AddTag;
using DocumentService.Features.Documents.Create;
using DocumentService.Features.Documents.GrantShare;
using DocumentService.Features.Documents.PutBody;
using DocumentService.Features.Documents.Update;
using DocumentService.Features.Documents.UpdateMetadata;
using DocumentService.Features.McpTools.Declare;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.PrivateNotes;
using DocumentService.Features.SyncDevices;
using DocumentService.Features.Tags;
using DocumentService.Features.Tags.Create;
using DocumentService.Features.Tags.Rename;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Domain.Ports;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;

const string ServiceName = "microservices-platform.document-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// FR-01, SC-05, SC-09, SC-10, #637: 取り込み経路で辞書に無いタグが現れた件数（0 が正常）。
builder.Services.AddMetrics();
builder.Services.AddSingleton<IngestTagMetrics>();
// FR-22, NFR-19, IADR-0215 決定 5-b (#600): 通知の送出結果（sent / rejected / unreachable）。
// **Meter 名は IngestTagMetrics と同じサービス名**なので収集対象は増えない。
builder.Services.AddSingleton<PrivateNoteNotificationMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(IngestTagMetrics.MeterName)
        .AddMeter(PrivateNoteNotificationMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR, #1012: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / E3a: Wolverine 発行側のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
// #269: MassTransit 側（DocumentNormalized 購読・DocumentUpdated 発行が残る間）のブローカ疎通は
// MassTransit 組み込みの "masstransit-bus"（tag "ready"）で満たす。
// 外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-05, FR-06, FR-09, FR-18, FR-19, FR-20, FR-21, UC-03, SC-05, SC-09 /
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1:
// 端点の入力検証。**アセンブリ走査（AddValidatorsFromAssembly）は使わない** —— 登録が暗黙になり、
// 検証器を消しても起動が通ってしまう（明示登録なら `IValidator<T>` の解決に失敗して止まる）。
builder.Services.AddScoped<IValidator<CreateDocumentRequest>, CreateDocumentValidator>();
builder.Services.AddScoped<IValidator<UpdateDocumentRequest>, UpdateDocumentValidator>();
builder.Services.AddScoped<IValidator<UpdateMetadataRequest>, UpdateMetadataValidator>();
builder.Services.AddScoped<IValidator<UpdateDocumentBodyRequest>, PutDocumentBodyValidator>();
builder.Services.AddScoped<IValidator<CreateShareRequest>, GrantDocumentShareValidator>();
builder.Services.AddScoped<IValidator<AddDocumentTagRequest>, AddDocumentTagValidator>();
builder.Services.AddScoped<IValidator<CreateTagRequest>, CreateTagValidator>();
builder.Services.AddScoped<IValidator<RenameTagRequest>, RenameTagValidator>();

// FR-06: Document DbContext (ADR-0002 Database per Service)
builder.Services.AddDbContext<DocumentDbContext>(opt => opt.UseNpgsql(connStr));

// FR-21, ADR-0014/ADR-0015: 文書本文の直接受け入れ経路が本文を格納する先（MinIO）。
// **バケットの作成（Bootstrap）はここでは行わない** —— 書き込み側の起動時保証は
// ConversionService が担っており（`AddPlatformObjectStorageBootstrap`）、同じバケットを
// 2 か所から作りにいく理由が無い。未設定の dev/test では縮退クライアントが登録される。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

// FR-19, FR-20, FR-22, [[IADR-0270]]: 個人資料（private-note）と Obsidian 同期の中核。
// - 監査ログ（同期・完全削除の「誰が・いつ・何件」。ADR-0037 決定 9）
// - 通知の発火側（検知は本サービス・実体は NotificationService。**送出は fail-open** であり、
//   届かなかったことはエラーログと計器（notification.dispatch.total）に残る。IADR-0215 決定 5-b）
// - 定期処理（90 日 purge・版刈り取り・通知検知）
builder.Services.AddSingleton<Platform.Shared.Infrastructure.Foundation.Audit.IAuditLogger,
    Platform.Shared.Infrastructure.Foundation.Audit.AuditLogger>();
builder.Services.AddHttpClient(
    DocumentService.Infrastructure.ExternalServices.HttpPrivateNoteNotifier.ClientName,
    c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:NotificationService"]
            ?? "http://notification-service:8080");
        // 🔴 既定の 100 秒のままにしない —— 受け口が応答しないとき、同期 push や完全削除の
        // 要求がその間止まる（fail-open は「落ちない」だけでなく「待たせない」ことも要る）。
        c.Timeout = DocumentService.Infrastructure.ExternalServices.HttpPrivateNoteNotifier.SendTimeout;
    });
builder.Services.AddScoped<DocumentService.Domain.Ports.IPrivateNoteNotifier,
    DocumentService.Infrastructure.ExternalServices.HttpPrivateNoteNotifier>();
// FR-06, FR-19, ADR-0057 決定 1, IADR-0296: 削除の伝播先①（オブジェクトストレージの本文・資産）。
// 台帳から逆引きして消すため DbContext と同じ scoped にする。
builder.Services.AddScoped<DocumentService.Features.Documents.DocumentObjectPurger>();
builder.Services.AddScoped<DocumentService.Features.PrivateNotes.Maintenance.PrivateNoteMaintenanceService>();
builder.Services.AddHostedService<
    DocumentService.Features.PrivateNotes.Maintenance.PrivateNoteMaintenanceHostedService>();

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit + RabbitMQ
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（catalog）の実効値を申告する。
builder.Services.AddPlatformIntrospection("document-service", pipeline,
    i => i.AddStep<DocumentService.Features.Documents.Catalog.DocumentNormalizedConsumer>());

// NFR, ADR-0027, #1022: ブローカ接続。**既定資格情報をイメージへ焼かない** —— appsettings.json からも
// 撤去したため、構成が注入されていなければここで落ちる（注入漏れが「既定の資格情報で接続成功」へ
// 倒れない。#1012 / IADR-0286 の DB と同型。IADR-0291）。**1 サービス 1 解決点にする。**
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（k8s は helm の global.messaging、compose は x-rabbit-env が注入する）。"
        + " 既定値は持たない —— 未注入をブローカへの接続失敗として現れさせないためである。");

builder.Services.AddMassTransit(x =>
{
    // FR-01, UC-04: 正規化文書をカタログへ登録する Consumer
    x.AddPlatformPipelineStep<DocumentService.Features.Documents.Catalog.DocumentNormalizedConsumer>(pipeline);
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitConnection);

        // ADR-0003（Superseded by ADR-0027・注記は #580）: 正規化文書のカタログ登録（DocumentNormalizedConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UsePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
});

// 🔴 ADR-0027 / E3a・E3b: **DocumentDeleted / DocumentUpdated の発行は Wolverine へ移した。**
// MassTransit に残るのは DocumentNormalized の購読（辺 E2）だけ ——
// 辺は原子的に動かす（IADR-0234 決定 3）ため、本サービスは移行期間中 **両スタックを同居させる**
// （E1 の ConversionService と同じ形。向きは逆で MT 購読 ＋ Wolverine 発行）。
builder.Services.AddScoped<IDocumentDeletedPublisher, WolverineDocumentDeletedPublisher>();
// ADR-0027 / E3b: DocumentUpdated の発行も Wolverine へ（IDocumentUpdatedPublisher）。
// MassTransit に残るのは DocumentNormalized の購読（辺 E2）だけになった。
builder.Services.AddScoped<IDocumentUpdatedPublisher, WolverineDocumentUpdatedPublisher>();
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "document-service";

    // 段をホストしないので探索は要らない。規約探索を切って明示配線に寄せる
    // （E1 の DataSourceService＝発行のみ、と同じ形）。
    opts.Discovery.DisableConventionalDiscovery();

    opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();

    // ADR-0027 手順 3（発行側）/ #992 / [[IADR-0314]]: **外向きの経路を宣言する。**
    // これが無いと `No routes can be determined for Envelope ...` を info ログへ 1 行出して
    // 黙って捨てられる（例外もヘルスチェックの赤も出ない。稼働 k3s で実測）。
    opts.RoutePlatformEvent<DocumentUpdated>();
    opts.RoutePlatformEvent<DocumentDeleted>();

    // 手順 4・5 ＋ retry/DLQ の共通既定（W1）。
    opts.UsePlatformMessagingDefaults();
});

var app = builder.Build();

// FR-06: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapDocumentEndpoints();
// FR-09, SC-05, SC-09, #634: タグ辞書（IADR-0152 決定 1）。
app.MapTagDictionaryEndpoints();
// FR-19, FR-20, ADR-0036 D-06, IADR-0253 決定 4（段 4）: 文書の共有先（所有者のみ変更可）。
app.MapDocumentShareEndpoints();
// FR-19, SC-19: 個人資料のライフサイクル（一覧・作成・削除・復元・完全削除・露出・容量）。
app.MapPrivateNoteEndpoints();
// FR-20, SC-20: 同期端末とトークン（発行・再発行・失効）。
app.MapSyncDeviceEndpoints();
// FR-20, ADR-0037: Obsidian プラグイン向け同期プロトコル（同期トークン認証）。
app.MapObsidianSyncEndpoints();
// FR-16, ADR-0024 §2: MCP ツール定義の自己申告（メッシュ内部限定。#1020）。
app.MapMcpToolEndpoints();

app.Run();

public partial class Program { }
