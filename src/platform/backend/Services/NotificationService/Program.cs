using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.Accept;
using NotificationService.Features.Notifications.DispatchEmails;
using NotificationService.Features.Notifications.PurgeExpired;
using NotificationService.Common.Observability;
using NotificationService.Common.Options;
using NotificationService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

// FR-22, IADR-0215 決定 1: 通知の送出主体（12 番目のサービス）。
// 送信上限（ADR-0045 決定 3）は**テナント全体で 1 つの資源**であり、消費を数える場所も 1 つでよい
// —— これが相乗り案ではなく新設を採った決め手である。
const string ServiceName = "microservices-platform.notification-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(NotificationDeliveryMetrics.MeterName));
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 3, [[IADR-0419]] (#1255):
// east-west gRPC の h2c リスナ（`Grpc:Port`。**未設定なら立てない**）。
// HTTP/1.1 のポート（REST の /notifications・受け口・/health/*・introspection）はそのまま残る。
builder.AddPlatformGrpcListener();

// NFR: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks().AddNpgSql(connStr, tags: ["ready"]);
builder.Services.AddOpenApi();

builder.Services.AddDbContext<NotificationDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.Configure<NotificationOptions>(
    builder.Configuration.GetSection(NotificationOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationDeliveryMetrics>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

// ADR-0045 / IADR-0215 決定 3: SMTP の実体はこの環境に無い。**未設定は「成功」ではなく
// 「送信できなかった」として outbox に failed を残す**（静かに落とさないため）。
builder.Services.AddScoped<IEmailTransport, UnconfiguredSmtpEmailTransport>();
// 宛先の解決元は未決（機能仕様書 §未決事項 2）。port だけを置く。
builder.Services.AddScoped<IEmailAddressResolver, UnresolvedEmailAddressResolver>();

builder.Services.AddScoped<NotificationStore>();
builder.Services.AddScoped<NotificationPublisher>();
// FR-22, IADR-0270 決定 6: 発火側（DocumentService）からの受け口。検知は向こう・実体はこちら。
builder.Services.AddScoped<NotificationIngress>();
// FR-22, 計画 ADR-0030 §決定 / IADR-0371 決定 2 / [[IADR-0398]] 決定 7: 受け口の入力検証。
// **1 検証器 1 行の明示登録**である（AddValidatorsFromAssembly は使わない —— この行を消したら
// 起動時ではなく最初の要求で 500 になり、既存の受け口テストが赤くなって気づける）。
builder.Services.AddScoped<IValidator<NotificationIngressRequest>, NotificationIngressValidator>();
builder.Services.AddScoped<EmailOutboxDispatcher>();
builder.Services.AddScoped<NotificationRetention>();
builder.Services.AddHostedService<NotificationMaintenanceHostedService>();

// FR-15, ADR-0018, IADR-0029: 自己申告（イントロスペクション）。段は持たないが到達可能性を申告する。
builder.Services.AddPlatformIntrospection("notification-service", new PipelineOptions());

var app = builder.Build();

// 起動時にスキーマを最新 Migration へ更新（AuthorizationService と同じ作法）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapNotificationEndpoints();
// FR-22: メッシュ内部限定の受け口（認証は課さない。OpenAPI には載せない）。
app.MapNotificationIngressEndpoints();
// FR-22, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0419]] (#1255):
// 同じ受け口の east-west gRPC 面。**本体（NotificationIngress.AcceptAsync）は上の REST と共有**し、
// 面は `ServiceCaller`（realm ロール platform-service）を要求する ——
// **REST の無認証の口はそのまま残す**（並走中の正は REST。切替は呼び出し元の構成 1 つで行う）。
app.MapGrpcService<NotificationIngressGrpcService>();

app.Run();

public partial class Program { }
