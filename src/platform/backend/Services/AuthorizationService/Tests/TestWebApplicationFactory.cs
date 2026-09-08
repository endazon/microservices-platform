using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.ExternalServices;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // NFR, #660: **各テストクラスで DB を分離するための一意名（InMemory）。**
    // xUnit はテストクラスごとに並列で走り、`IClassFixture` はクラスごとに別インスタンスを作る。
    // ところが DB 名が固定だと**ストアだけがプロセス内で共有され**、
    // 他クラスの書き込みが見えてしまう（`AuthzTest` で実際に発火した。#660）。
    private readonly string _dbName = $"AuthzTest_{Guid.NewGuid()}";

    // FR-05, NFR-09, 計画 ADR-0088 決定 1, [[IADR-0413]] (#1333):
    // 🔴 **ABAC 判定に使う属性は IdP から引き直される。要求本文の主張は評価に用いられない。**
    // 属性・不在・失敗はここへ置く（規則は `TestIdentityDirectory` が 1 つだけ持つ）。
    public TestIdentityDirectory Identity { get; } = new();

    /// <summary>`ServiceCaller`（realm ロール `platform-service`）として叩くクライアント。</summary>
    public HttpClient CreateServiceCallerClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.ServiceRole);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<AuthorizationDbContext>(services, _dbName);

            // FR-09: 管理系エンドポイントは AdminOnly を要求する。テストでは Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // #1333: 引き直しの後段だけを差し替える（規則は TestIdentityDirectory が持つ）。
            TestIdentityDirectory.Replace(services, Identity);
        });
    }

    // #1201: gRPC 用の Kestrel 器（GrpcKestrelFactory）も同じ差し替えを使う。
    internal static void ReplaceDbContext<TContext>(IServiceCollection services, string dbName)
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
