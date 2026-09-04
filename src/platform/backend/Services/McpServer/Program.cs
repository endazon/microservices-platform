using System.Security.Claims;
using System.Text.Json;
using McpServer.Features.Tools;
using McpServer.Features.Tools.CallTool;
using McpServer.Features.Tools.ListTools;
using McpServer.Features.McpClients;
using McpServer.Infrastructure.Persistence;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

const string ServiceName = "microservices-platform.mcp-server";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
// NFR: 接続先は構成から受け取る。**既定の資格情報を埋め込まない。**
// 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れ、
// 誤った DB へ書き込んだまま健全に見える。ここで落ちれば配備の誤りはその場で判る。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection が未設定である（環境変数 "
        + "ConnectionStrings__DefaultConnection で注入する）。");

builder.Services.AddPlatformHealthChecks().AddNpgSql(connStr, tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-16, UC-09: MCP クライアント登録簿
builder.Services.AddDbContext<McpDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();

// 🔴 FR-16, FR-05, UC-09, SC-12, ADR-0062 決定 2・3: 無人アカウントの `clearance` / タグは
// **登録者が持つ集合の部分集合**でなければならず、その判定は後段（ここ）が行う。
// 登録者の属性の正は認可サービスであり、身元の口（/bff/auth/me）へは配らない（同 決定 4）。
//
// 既定は 8080 とする（compose も k8s も 8080 で上書きしている。コード既定の :5005 は古く、
// 新規に口を開く側で写すと配備の上書き漏れが「名前解決は通るがポートが無い」形で沈黙する）。
builder.Services.AddHttpClient(
    AuthorizationServiceRegistrarAttributes.HttpClientName,
    c => c.BaseAddress = new Uri(builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service:8080"));
// 呼び出し元の Authorization を後段へ転送するために要る（サービス専用の資格情報を新設しない）。
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRegistrarAttributeResolver, AuthorizationServiceRegistrarAttributes>();

// FR-16, ADR-0024: 宣言的公開構成・自己申告の集約・実効ツール一覧
builder.Services.AddSingleton<ToolPublicationConfigLoader>();
builder.Services.AddSingleton<ToolCatalog>();
builder.Services.AddScoped<IToolDeclarationSource, HttpToolDeclarationSource>();
builder.Services.AddHostedService<ToolCatalogRefresher>();

// FR-16, UC-08: ツール呼び出しの単一経路（登録確認 → 公開確認 → 除外 → 越境 → 監査）
builder.Services.AddSingleton<ServiceAccountDocumentFilter>();
builder.Services.AddSingleton<EgressPolicy>();
builder.Services.AddScoped<IToolInvoker, HttpToolInvoker>();
builder.Services.AddScoped<McpSubjectResolver>();
builder.Services.AddScoped<ToolInvocationService>();
// ADR-0065 決定 2: プロトコル面のハンドラは操作フォルダ（Features/Tools/{ListTools,CallTool}）へ
// 分かれている。統制の単一経路（ToolInvocationService）はどちらも同じものを使う。
builder.Services.AddScoped<McpListToolsHandler>();
builder.Services.AddScoped<McpCallToolHandler>();

// FR-15, ADR-0018: 自己申告（イントロスペクション）。段は持たないが、実効ツール一覧の供給元として
// 到達可能性を申告する（ADR-0024 §5「実効ツール一覧は構成情報 API へ申告する」）。
builder.Services.AddPlatformIntrospection("mcp-server", new PipelineOptions());

// FR-16, ADR-0024: MCP サーバー本体。Streamable HTTP（11_mcp-server-integration §前提）。
// 🔴 ツールは動的ハンドラで供給する（コア改修なしでの追従。§2）。
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithListToolsHandler((ctx, ct) =>
        ctx.Services!.GetRequiredService<McpListToolsHandler>()
            .ListToolsAsync(ctx.User ?? new ClaimsPrincipal(), ct))
    .WithCallToolHandler((ctx, ct) =>
        ctx.Services!.GetRequiredService<McpCallToolHandler>().CallToolAsync(
            ctx.User ?? new ClaimsPrincipal(),
            ctx.Params?.Name,
            ctx.Params?.Arguments is { } args ? JsonSerializer.Serialize(args) : "{}",
            ct));

var app = builder.Build();

// 🔴 FR-16, ADR-0024 §5: 公開構成の検証は**ここで**行う（#445 レビュー指摘）。
// ToolCatalogRefresher は BackgroundService であり、その ExecuteAsync は IHost の起動を塞がない。
// 収集の定期実行に検証を任せると、壊れた構成でも Web サーバーは起動し、カタログが空のまま
// 「公開されているつもりの公開されていない」状態でヘルスチェックだけ緑になる。
// **要求を受ける前に落とす**ことでしか「逸脱が起動時に止まる」は成立しない。
app.Services.GetRequiredService<ToolPublicationConfigLoader>().Load();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UsePlatformMiddleware();
app.MapPlatformHealthChecks();
app.MapPlatformIntrospection();
app.MapOpenApi();

// ADR-0021: 入口は Istio Ingress Gateway の `/mcp` パス。
app.MapMcp("/mcp").RequireAuthorization();

app.MapMcpClientEndpoints();

app.Run();

public partial class Program { }
