using DocumentService.Api.Foundation.Observability;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using DocumentService.Api.Foundation.Endpoints;
using DocumentService.Api.Foundation.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using MassTransit;
using Microsoft.EntityFrameworkCore;

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
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=document_svc;Username=kp;Password=kp",
        tags: ["ready"]);
// #269: ブローカ疎通の readiness は MassTransit 組み込みの "masstransit-bus"（tag "ready"）で満たす。
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

// ADR-0003（Superseded by ADR-0027・注記は #580）: MassTransit + RabbitMQ
// FR-14, ADR-0018: 宣言的パイプライン構成（pipeline.json）。GitOps 配送された構成があれば読み込む。
builder.AddPlatformPipelineConfig();
var pipeline = builder.Configuration.GetPlatformPipeline();

// FR-15, ADR-0018: 自己申告（イントロスペクション）— この段（catalog）の実効値を申告する。
builder.Services.AddPlatformIntrospection("document-service", pipeline,
    i => i.AddStep<DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer>());

builder.Services.AddMassTransit(x =>
{
    // FR-01, UC-04: 正規化文書をカタログへ登録する Consumer
    x.AddPlatformPipelineStep<DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer>(pipeline);
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:ConnectionString"]
            ?? "amqp://guest:guest@rabbitmq:5672");

        // ADR-0003（Superseded by ADR-0027・注記は #580）: 正規化文書のカタログ登録（DocumentNormalizedConsumer）の一時的失敗を再試行し、
        // 継続失敗はデッドレターへ退避して回復性を確保する（共通設定）。
        cfg.UsePlatformRetry();

        cfg.ConfigureEndpoints(ctx);
    });
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

app.Run();

public partial class Program { }
