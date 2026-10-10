using System.Security.Claims;
using DocumentService.Infrastructure.Persistence;
using GraphService.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Knowledge.IntegrationTests.McpTools;

// FR-16, ADR-0024 §2 (#1020): 自己申告の面（gRPC `McpToolDeclarations/Declare`。［2026-10-10 / #1517］REST の
// `GET /internal/mcp-tools` は撤去した）を持つサービスを
// **in-process で**起こす器。
//
// 🔴 **Docker を要求しない。** Testcontainers（`PostgresFixture` / `RabbitMqFixture`）を使わず、
// DB は InMemory・ブローカは外部トランスポートを切って起動する。測るのは「申告 → 収集 → 突合」
// であって永続化やメッセージングではないため、実コンテナを要る形にすると
// **`integration.yml`（develop への push と日次のみ・PR では走らない）でしか実走しなくなる。**
// この器なら PR の `ci` ジョブ（`dotnet test <unit>/backend/backend.slnx`）で毎回走る。
//
// 🔴 **構成は `UseSetting` で与える。`ConfigureAppConfiguration` では間に合わない。**
// 3 サービスの `Program.cs` はトップレベル文で `ConnectionStrings:DefaultConnection`（#1012）と
// `RabbitMq:ConnectionString`（#1022）を**ビルダ構築中に即座に読み**、未設定なら例外で落ちる。
// `ConfigureAppConfiguration` で足した値が見えるのはその後である。
// `IntegrationTestFactory.cs` が記録している 3 件の実測事故（`Pipeline:ConfigPath` /
// `RabbitMq:ConnectionString` / `ConnectionStrings:DefaultConnection`）と同型の罠であり、
// **「統合テストの config 上書きは効く」は一般化できない —— 読まれる時点で決まる。**
//
// 🔴 **環境変数（`[ModuleInitializer]`）は使わない。** 同じキーを Docker 依存の既存テストが
// フィクスチャから `UseSetting` で与えている。プロセス全体へ env を置くと、フィクスチャの
// 起動失敗と構成の注入漏れが読み分けられなくなる（#1032 の再発）。**この器の中だけで与える。**
internal abstract class McpToolDeclarationHost<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    // 収集側が引く「サービス → 宛先」の host 部分。実 DNS は引かない（チャネルはテストサーバーの handler へ向ける）。
    internal abstract string MeshHost { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // ★読まれる時点に間に合わせる（上の注記）。資格情報を持たない到達不能な値で足りる。
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Host=localhost;Database=mcp_decl_{Guid.NewGuid():N}");
        builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost:5672");

        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Qdrant:Host"] = "localhost",
                ["Qdrant:Port"] = "6334",
                ["Services:LlmGateway"] = "http://localhost:5007",
                ["Services:AuthorizationService"] = "http://localhost/authz",
            }));

        builder.ConfigureServices(services =>
        {
            // 🔴 これが無いとテストホストの起動が実ブローカへ接続を試み、約 135 秒ハングする
            // （DocumentService.Tests / GraphService.Tests / RetrievalService.Tests の実測と同型）。
            services.DisableAllExternalWolverineTransports();
            // [[IADR-0533]] (#1517): 申告の面は ServiceCaller を要求する。本器の関心は「申告 → 収集 → 突合」なので
            // 認可の評価を通過させる（s2s の要求そのものは各サービスの `GrpcMcpToolDeclarationTests` が実 Kestrel で測る）。
            services.AddSingleton<IPolicyEvaluator, PassThroughPolicyEvaluator>();
            ConfigureService(services);
        });
    }

    protected virtual void ConfigureService(IServiceCollection services) { }

    protected static void ReplaceDbContext<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        var name = $"McpDecl_{typeof(TContext).Name}_{Guid.NewGuid()}";
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                                .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(name));
    }
}

// 認証・認可の評価を常に通す（本器専用。s2s の主体名だけを載せる）。
internal sealed class PassThroughPolicyEvaluator : IPolicyEvaluator
{
    public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "service-account-mcp-server")], "McpDeclarationHost"));
        context.User = principal;
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "McpDeclarationHost")));
    }

    public Task<PolicyAuthorizationResult> AuthorizeAsync(
        AuthorizationPolicy policy, AuthenticateResult authenticationResult, HttpContext context, object? resource) =>
        Task.FromResult(PolicyAuthorizationResult.Success());
}

// global:: でローカル namespace（Knowledge.IntegrationTests.*）を隠さないようにする。
internal sealed class DocumentServiceDeclarationHost
    : McpToolDeclarationHost<global::DocumentService.DocumentServiceTestMarker>
{
    internal override string MeshHost => "document-service";

    protected override void ConfigureService(IServiceCollection services)
    {
        ReplaceDbContext<DocumentDbContext>(services);
        // 本サービスは MassTransit も起こす（DocumentNormalized の購読が残る）。実ブローカへ
        // 繋がないようテストハーネスへ差し替える（DocumentService.Tests と同じ作法）。
        services.RemoveAll<IBusControl>();
        services.AddMassTransitTestHarness();
    }
}

internal sealed class GraphServiceDeclarationHost
    : McpToolDeclarationHost<global::GraphService.GraphServiceTestMarker>
{
    internal override string MeshHost => "graph-service";

    protected override void ConfigureService(IServiceCollection services)
        => ReplaceDbContext<GraphDbContext>(services);
}

// RetrievalService は DbContext を持たない（索引は Qdrant 側）。マーカー型も持たないため、
// **同アセンブリの公開型**をエントリポイントの手掛かりに使う（`WebApplicationFactory` は
// 型そのものではなく `typeof(T).Assembly` からエントリポイントを解決する）。
internal sealed class RetrievalServiceDeclarationHost
    : McpToolDeclarationHost<global::RetrievalService.Infrastructure.ExternalServices.InMemoryVectorStore>
{
    internal override string MeshHost => "retrieval-service";
}
