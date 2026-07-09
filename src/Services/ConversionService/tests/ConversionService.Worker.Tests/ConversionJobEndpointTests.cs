using System.Net;
using System.Net.Http.Json;
using ConversionService.Worker.Foundation.Jobs;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ照会・人手補正エンドポイントが状況一覧（絞り込み含む）・
// 個別取得・再変換（202/404）を提供することを検証する。RabbitMQ は使わず MassTransit テストハーネスに差し替える。
// 各テストは singleton ストアの状態が独立するよう Factory を都度生成する。
public class ConversionJobEndpointTests
{
    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    [Fact]
    public async Task GetList_ReturnsSeededJobs_AndFiltersByStatus()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IConversionJobStore>();

        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        store.Start(Raw(ok));
        store.Succeed(ok, Guid.NewGuid(), "storage://ok.md");
        store.Start(Raw(bad));
        store.Fail(bad, "変換失敗");

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
        var store = factory.Services.GetRequiredService<IConversionJobStore>();
        var id = Guid.NewGuid();
        store.Start(Raw(id));

        (await client.GetAsync($"/jobs/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/jobs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_KnownFailedJob_Returns202_AndRequeues()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IConversionJobStore>();
        var id = Guid.NewGuid();
        store.Start(Raw(id));
        store.Fail(id, "失敗");

        var resp = await client.PostAsync($"/jobs/{id}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        store.Get(id)!.Status.Should().Be(ConversionJobStatus.Queued);
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
                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える（再変換の Publish 用）。
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness();
            });
        }
    }
}
