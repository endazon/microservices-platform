using System.Net;
using System.Net.Http.Json;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブ照会・人手補正エンドポイントが状況一覧（絞り込み含む）・
// 個別取得・再変換（202/404/409）を提供することを検証する。RabbitMQ は使わず MassTransit テストハーネスに、
// DB は EF Core InMemory provider に差し替える。各テストは Factory を都度生成し状態を独立させる。
public class ConversionJobEndpointTests
{
    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // 変換ジョブをストア（スコープ）経由で投入する。InMemory DB は Factory の provider 内で共有され、
    // 後続の HTTP リクエスト（別スコープ）から参照できる。
    private static async Task SeedAsync(Factory factory, Func<IConversionJobStore, Task> seed)
    {
        using var scope = factory.Services.CreateScope();
        await seed(scope.ServiceProvider.GetRequiredService<IConversionJobStore>());
    }

    [Fact]
    public async Task GetList_ReturnsSeededJobs_AndFiltersByStatus()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();

        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(ok));
            await store.SucceedAsync(ok, Guid.NewGuid(), "storage://ok.md");
            await store.StartAsync(Raw(bad));
            await store.FailAsync(bad, "変換失敗");
        });

        var all = await client.GetFromJsonAsync<List<ConversionJobDto>>("/jobs");
        all!.Should().HaveCount(2);

        var failed = await client.GetFromJsonAsync<List<ConversionJobDto>>("/jobs?status=failed");
        failed!.Should().ContainSingle(j => j.Id == bad).Which.Error.Should().Be("変換失敗");
    }

    [Fact]
    public async Task GetById_ReturnsJob_Or404()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, store => store.StartAsync(Raw(id)));

        (await client.GetAsync($"/jobs/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/jobs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_KnownFailedJob_Returns202()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(id));
            await store.FailAsync(id, "失敗");
        });

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null);

        // HTTP 契約（失敗ジョブは受理＝202）のみを検証する。queued への遷移は、再発行イベントが
        // テストハーネスのコンシューマに即時消費されて processing 等へ進み得るため、ここで status を
        // 断定するとレース（フレーク）になる。queued 化は決定的な store 単体テスト
        // （PrepareRetry_requeues_and_returns_original_event）で担保する。
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Retry_NonFailedJob_Returns409()
    {
        // UC-06: 失敗以外（ここでは succeeded）は再変換不可＝409。UI 制御に頼らず API 側で状態を強制する。
        using var factory = new Factory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(factory, async store =>
        {
            await store.StartAsync(Raw(id));
            await store.SucceedAsync(id, Guid.NewGuid(), "storage://ok.md");
        });

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var job = await client.GetFromJsonAsync<ConversionJobDto>($"/jobs/{id}");
        job!.Status.Should().Be(ConversionJobStatus.Succeeded); // 状態は変わらない
    }

    [Fact]
    public async Task Retry_UnknownJob_Returns404()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync($"/jobs/{Guid.NewGuid()}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        // Factory ごとに一意な InMemory DB 名で他テストと隔離する。
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317"
                }));
            builder.ConfigureServices(services =>
            {
                // Npgsql DbContext を InMemory へ差し替える（起動時 MigrateAsync は IsRelational=false で回避）。
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>(_dbName);

                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える（再変換の Publish 用）。
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
            });
        }
    }
}
