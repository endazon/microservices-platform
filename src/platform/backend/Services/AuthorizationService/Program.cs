using AuthorizationService.Features.Authz;
using AuthorizationService.Features.Authz.ResolveScope;
using AuthorizationService.Features.Users;
using AuthorizationService.Infrastructure.ExternalServices;
using AuthorizationService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "microservices-platform.authorization-service";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3 (#1201): east-west gRPC の h2c リスナ（`Grpc:Port`。
// 未設定なら立てない）。HTTP/1.1 のポート（REST・/health/*）はそのまま残る。
builder.AddPlatformGrpcListener();
// NFR, #1012: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        connStr,
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-05: ABAC DbContext
builder.Services.AddDbContext<AuthorizationDbContext>(opt => opt.UseNpgsql(connStr));

// FR-05, FR-09, SC-17, IADR-0301: 利用者アカウント管理の反映先（身元プロバイダ）。
// **`IdentityAdmin:Provider` に既定は無い** —— 未設定ならここで落ちる。既定を in-memory にすると
// 注入漏れが「反映したつもりで消える」へ倒れ、既定を keycloak にすると資格情報未整備の配備が
// 起動できなくなる。どちらの既定も誤りなので、宣言そのものを配備側へ出す。
// keycloak を選んだときの資格情報も既定を持たない（#1012 / IADR-0286 と同型）。
//
// 🔴 IADR-0329 (#1101): **`in-memory` は Development でしか選べない。** 実行環境を渡すのは
// そのためである（Development 以外で偽物を宣言したらここで落ちる）。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddIdentityAdminClient(builder.Configuration, builder.Environment);

// FR-15, ADR-0018, IADR-0029 (#143): 自己申告（イントロスペクション）。段・合成可能ポートは
// ホストしないが、到達可能性とトポロジ（段なし）を実効構成へ与えるため存在申告する。
builder.Services.AddPlatformIntrospection("authorization-service", new PipelineOptions());

var app = builder.Build();

// FR-05: 起動時にスキーマを最新 Migration へ更新
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

app.MapAuthzEndpoints();
// FR-05, ADR-0029, ADR-0075, IADR-0379 (#1201): `/authz/scope` の gRPC 面（参照実装）。REST と同じ評価器を呼ぶ。
// 呼び出し側サービスの資格情報（ServiceCaller ポリシー）を要求する —— 利用者のトークンでは通らない。
app.MapGrpcService<AuthzScopeGrpcService>();
// FR-05, FR-09, UC-05, SC-17: 利用者アカウント管理（AdminOnly）。
app.MapUserAdminEndpoints();

app.Run();

public partial class Program { }
