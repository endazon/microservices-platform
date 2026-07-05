using KnowledgePlatform.Shared.Contracts.Dtos;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WikiService.Api.Infrastructure;
using WikiService.Api.Services;

namespace WikiService.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    // FR-13/FR-05: 閲覧テストで使う ABAC 許可スコープ。既定は全件許可（Health/一覧テスト用）。
    // ABAC テストはテストごとに差し替える（クラス内テストは直列実行のため安全）。
    public AccessScopeResponse Scope { get; set; } = new("test-user", [], true);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<WikiDbContext>(services, "WikiTest");

            // FR-13: ABAC 解決を HTTP に依存させず、テスト制御可能なスタブへ差し替える。
            services.RemoveAll<IWikiAccessResolver>();
            services.AddSingleton<IWikiAccessResolver>(new StubWikiAccessResolver(this));

            // IADR-0020/0021: 認可プロキシの本文取得を稼働 Wiki.js に依存させず、スタブへ差し替える。
            // GetRenderedContentAsync は常に本文を返し、ABAC 通過ページのみ 200 になることを検証可能にする。
            services.RemoveAll<IWikiJsClient>();
            services.AddSingleton<IWikiJsClient>(new StubWikiJsClient());

            services.RemoveAll<IBusControl>();
            services.AddMassTransitTestHarness();
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

// FR-13, FR-05: 認可解決のテスト用スタブ。ファクトリの現在の Scope を返す。
file class StubWikiAccessResolver(TestWebApplicationFactory factory) : IWikiAccessResolver
{
    public Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
        => Task.FromResult(factory.Scope);
}

// FR-13, IADR-0020/0021: Wiki.js 同期・本文取得のテスト用スタブ。
// 本文は常に返す（ABAC 到達可否の検証は WikiEndpoints 側の判定に委ねる）。
file class StubWikiJsClient : IWikiJsClient
{
    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>($"<article data-path=\"{path}\">rendered</article>");
}
