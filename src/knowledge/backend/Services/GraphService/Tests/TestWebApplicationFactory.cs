using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using Wolverine;

namespace GraphService.Tests;

// FR-17: GraphService の統合テスト用ホスト。
//
// ABAC スコープは差し替え可能にする（既定は「条件無しで全許可」）。認可サービスへの実通信は
// 行わない —— GraphAccessResolver 自体の deny-closed 縮退は GraphAccessResolverTests が
// HttpMessageHandler 層で直接試験する。
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // 各テストクラスで DB を分離するための一意名（InMemory）。
    // 固定名にすると xUnit のクラス並列実行で書き込みが混ざる。
    private readonly string _dbName = $"GraphTest_{Guid.NewGuid()}";

    // 既定は「マッチするポリシーがあり、条件は無し」＝全許可。**read のスコープ。**
    public Func<HttpContext, AccessScopeResponse> ScopeProvider { get; set; } =
        _ => new AccessScopeResponse("test-user", [], true);

    // #993, IADR-0272 決定 2: **write のスコープ**（書き込み経路が解決するもの）。
    // null なら ScopeProvider へ委譲する —— 既存テストは read だけを差し替えて書けたままになる。
    // **書き込みの認可を測るテストは、必ずここを明示的に置くこと。**
    public Func<HttpContext, AccessScopeResponse>? WriteScopeProvider { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:AuthorizationService"] = "http://localhost/authz",
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<GraphDbContext>(services, _dbName);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ABAC スコープを差し替える。
            services.RemoveAll<IGraphAccessResolver>();
            services.AddScoped<IGraphAccessResolver>(_ => new StubAccessResolver(this));

            // ADR-0027 / #1016: graph-delete 段の購読は Wolverine。
            // 🔴 **これが無いとテストが約 135 秒ハングする** —— Program.cs が UseWolverine +
            // UseRabbitMq を呼ぶため、テストホストの起動が実ブローカへの接続を試みる
            // （E1 の DataSourceService.Tests と同じ作法）。
            services.DisableAllExternalWolverineTransports();
        });
    }

    // テストデータの投入。DbContext を直接触る（#908 はイベント購読を持たないため）。
    public async Task SeedAsync(Func<GraphDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    private sealed class StubAccessResolver(TestWebApplicationFactory owner) : IGraphAccessResolver
    {
        public Task<AccessScopeResponse> ResolveAsync(
            HttpContext ctx, string action, CancellationToken ct = default)
            => Task.FromResult(action == GraphAccessAction.Write
                ? (owner.WriteScopeProvider ?? owner.ScopeProvider)(ctx)
                : owner.ScopeProvider(ctx));
    }

    private static void ReplaceDbContext<TContext>(IServiceCollection services, string dbName)
        where TContext : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
