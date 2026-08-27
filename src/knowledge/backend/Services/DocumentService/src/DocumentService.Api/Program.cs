using DocumentService.Api.Foundation.Observability;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using DocumentService.Api.Composable.Adapters;
using DocumentService.Api.Foundation.Endpoints;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Application.Foundation.Ports;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.RabbitMQ;

const string ServiceName = "microservices-platform.document-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);

// FR-01, SC-05, SC-09, SC-10, #637: 取り込み経路で辞書に無いタグが現れた件数（0 が正常）。
builder.Services.AddMetrics();
builder.Services.AddSingleton<IngestTagMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(IngestTagMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    // ADR-0027 / E3a: Wolverine 発行側のブローカ疎通を readiness へ載せる（W4）。
    // Wolverine 側は自動登録しないので明示的に足す（無いとブローカ不達でも /health/ready が 200）。
    .AddPlatformWolverineBroker()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp",
        tags: ["ready"]);
// #269: MassTransit 側（DocumentNormalized 購読・DocumentUpdated 発行が残る間）のブローカ疎通は
// MassTransit 組み込みの "masstransit-bus"（tag "ready"）で満たす。
// 外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。
builder.Services.AddOpenApi();

// FR-06: Document DbContext (ADR-0002 Database per Service)
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<DocumentDbContext>(opt => opt.UseNpgsql(connStr));

// FR-21, ADR-0014/ADR-0015: 文書本文の直接受け入れ経路が本文を格納する先（MinIO）。
// **バケットの作成（Bootstrap）はここでは行わない** —— 書き込み側の起動時保証は
// ConversionService が担っており（`AddPlatformObjectStorageBootstrap`）、同じバケットを
// 2 か所から作りにいく理由が無い。未設定の dev/test では縮退クライアントが登録される。
builder.Services.AddPlatformObjectStorage(builder.Configuration);

// FR-19, FR-20, FR-22, [[IADR-0270]]: 個人資料（private-note）と Obsidian 同期の中核。
// - 監査ログ（同期・完全削除の「誰が・いつ・何件」。ADR-0037 決定 9）
// - 通知の発火側（検知は本サービス・実体は NotificationService。受け口が入るまで送出失敗は
//   エラーログに記録される）
// - 定期処理（90 日 purge・版刈り取り・通知検知）
builder.Services.AddSingleton<Platform.Shared.Infrastructure.Foundation.Audit.IAuditLogger,
    Platform.Shared.Infrastructure.Foundation.Audit.AuditLogger>();
builder.Services.AddHttpClient(
    DocumentService.Api.Foundation.Services.HttpPrivateNoteNotifier.ClientName,
    c => c.BaseAddress = new Uri(builder.Configuration["Services:NotificationService"]
        ?? "http://notification-service:8080"));
builder.Services.AddScoped<DocumentService.Api.Foundation.Ports.IPrivateNoteNotifier,
    DocumentService.Api.Foundation.Services.HttpPrivateNoteNotifier>();
builder.Services.AddScoped<DocumentService.Api.Foundation.Services.PrivateNoteMaintenanceService>();
builder.Services.AddHostedService<
    DocumentService.Api.Foundation.Services.PrivateNoteMaintenanceHostedService>();

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit + RabbitMQ
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（catalog）の実効値を申告する。
builder.Services.AddPlatformIntrospection("document-service", pipeline,
    i => i.AddStep<DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer>());

var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? "amqp://guest:guest@rabbitmq:5672";

builder.Services.AddMassTransit(x =>
{
    // FR-01, UC-04: 正規化文書をカタログへ登録する Consumer
    x.AddPlatformPipelineStep<DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer>(pipeline);
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

app.Run();

public partial class Program { }
