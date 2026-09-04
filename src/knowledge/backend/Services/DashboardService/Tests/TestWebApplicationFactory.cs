using DashboardService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardService.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // 各テストクラスで DB を分離するための一意名（InMemory）。
    private readonly string _dbName = $"DashboardTest_{Guid.NewGuid()}";

    // FR-10, ADR-0071 (#1197): 構成の上書き。**配備時の構成で変更できる**という決定
    // （ADR-0071 決定 1 末尾）は、上書きが実際に効くことを測らない限り宣言に過ぎない。
    //
    // **コンストラクタ引数ではなく `init` プロパティにしてある。** 本クラスは
    // `IClassFixture<TestWebApplicationFactory>` としても使われており、xUnit は
    // **引数を解決できないコンストラクタを持つ型をフィクスチャにできない**（実測: 引数付きに
    // した時点で HealthEndpointTests / IntrospectionEndpointTests が
    // 「unresolved constructor arguments」で落ちた）。
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                // FR-10, ADR-0072 決定 3 (#1198): **保持期間の掃除は既定で止める。**
                // 器がホストを起こすたびに背景で削除が走ると、投入した行がテストの
                // 途中で消え得る（`NotificationOptions.MaintenanceEnabled` と同じ理由）。
                // 掃除そのものは `UsageEventRetention` を直接呼んで測る。
                ["UsageRetention:Enabled"] = "false"
            });
            // **既定の後に足す**（後勝ち）。テストが指定した値が実際に効く順序である。
            if (Settings is { Count: > 0 })
                cfg.AddInMemoryCollection(Settings);
        });
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<DashboardDbContext>(services, _dbName);

            // FR-10: 集計 (GET /dashboard/*) は管理系ロール（admin ＋ operator。#544）を要求する。テストでは Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
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
