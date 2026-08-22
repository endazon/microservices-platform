using System.Security.Claims;
using System.Text.Json;
using McpServer.Api.Composable.Mcp;
using McpServer.Api.Foundation.Endpoints;
using McpServer.Api.Foundation.Persistence;
using McpServer.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

const string ServiceName = "microservices-platform.mcp-server";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);

builder.Services.AddPlatformObservability(builder.Configuration, ServiceName);
builder.Services.AddPlatformAuth(builder.Configuration);
builder.Services.AddPlatformHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=postgres;Port=5432;Database=mcp_svc;Username=kp;Password=kp",
        tags: ["ready"]);
builder.Services.AddOpenApi();

// FR-16, UC-09: MCP クライアント登録簿
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=mcp_svc;Username=kp;Password=kp";
builder.Services.AddDbContext<McpDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();

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
builder.Services.AddScoped<McpToolHandlers>();

// FR-15, ADR-0018: 自己申告（イントロスペクション）。段は持たないが、実効ツール一覧の供給元として
// 到達可能性を申告する（ADR-0024 §5「実効ツール一覧は構成情報 API へ申告する」）。
builder.Services.AddPlatformIntrospection("mcp-server", new PipelineOptions());

// FR-16, ADR-0024: MCP サーバー本体。Streamable HTTP（11_mcp-server-integration §前提）。
// 🔴 ツールは動的ハンドラで供給する（コア改修なしでの追従。§2）。
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithListToolsHandler((ctx, ct) =>
        ctx.Services!.GetRequiredService<McpToolHandlers>()
            .ListToolsAsync(ctx.User ?? new ClaimsPrincipal(), ct))
    .WithCallToolHandler((ctx, ct) =>
        ctx.Services!.GetRequiredService<McpToolHandlers>().CallToolAsync(
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
