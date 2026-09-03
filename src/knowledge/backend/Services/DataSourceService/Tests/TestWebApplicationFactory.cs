using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Persistence;
using Wolverine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataSourceService.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // NFR, #660: **各テストクラスで DB を分離するための一意名（InMemory）。**
    // xUnit はテストクラスごとに並列で走り、`IClassFixture` はクラスごとに別インスタンスを作る。
    // ところが DB 名が固定だと**ストアだけがプロセス内で共有され**、
    // 他クラスの書き込みが見えてしまう（`DataSourceTest` で実際に発火した。#660）。
    private readonly string _dbName = $"DataSourceTest_{Guid.NewGuid()}";

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
            ReplaceDbContext<DataSourceDbContext>(services, _dbName);

            // FR-09, IADR-0044: /datasources は admin/operator を要求する。Keycloak/JWT に依存せず
            // TestAuthHandler で認証し、既定で管理者ロールを付与する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ADR-0027（#441 E1）: 本サービスの発行は Wolverine へ移った。
            // 実ブローカへ繋がずに「何を発行したか」だけを観測するため、IMessageBus を差し替える。
            // 🔴 実 Wolverine ホストは起こさない —— テストの目的は発行内容の固定であって、
            // 実ブローカ越しの配送は Knowledge.IntegrationTests の実ブローカ試験が測る。
            // 🔴 ADR-0027（#441 E1）: **これが無いとテストが約 135 秒ハングする。**
            // 本番の Program.cs が UseWolverine + UseRabbitMq を呼ぶため、テストホストの起動が
            // 実ブローカへの接続を試み、**20 回再試行して BrokerInitializationException で失敗する**
            // （W4 で実測した挙動）。外部トランスポートを無効化して、起動を実ブローカから切り離す。
            services.DisableAllExternalWolverineTransports();

            // FR-05, SC-06, ADR-0074 決定 4 (#1194): 写像先の実在検証の後段（AuthorizationService の
            // /authz/users）へ HTTP を出さない。**判断の側だけをテストで固定する。**
            // 🔴 シングルトンで差す —— テストは `factory.Services` から同じ実体を掴み、
            // 名簿の中身と `Available` を要求の前に切り替える。
            services.RemoveAll<IPlatformUserDirectory>();
            services.AddSingleton<StubPlatformUserDirectory>();
            services.AddSingleton<IPlatformUserDirectory>(sp => sp.GetRequiredService<StubPlatformUserDirectory>());

            services.RemoveAll<IMessageBus>();
            services.AddSingleton<RecordingMessageBus>();
            services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RecordingMessageBus>());
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
