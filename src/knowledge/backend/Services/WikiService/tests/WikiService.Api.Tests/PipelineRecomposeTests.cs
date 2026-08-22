using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WikiService.Api.Composable.Steps;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;

namespace WikiService.Api.Tests;

// FR-14, ADR-0018, IADR-0028: コード改修なしの組み替えの実証。
// 同一サービス内の 2 段（wiki-sync / wiki-delete）を宣言的構成のみで選択的に有効化し、
// 無効化した段の購読が生成されない（イベントが処理されない）ことを検証する。
public class PipelineRecomposeTests
{
    private static PipelineOptions SyncDisabledDeleteEnabled() => new()
    {
        Steps =
        [
            new PipelineStepOptions
            {
                Name = "wiki-sync",
                Service = "wiki-service",
                Consumer = "WikiService.Api.Composable.Steps.DocumentSyncConsumer",
                Input = "DocumentUpdated",
                Outputs = [],
                Enabled = false,
            },
            new PipelineStepOptions
            {
                Name = "wiki-delete",
                Service = "wiki-service",
                Consumer = "WikiService.Api.Composable.Steps.DocumentDeletedConsumer",
                Input = "DocumentDeleted",
                Outputs = [],
                Enabled = true,
            },
        ],
    };

    [Fact]
    public async Task 構成のみで同期段を外し削除段だけを有効化できる()
    {
        var pipeline = SyncDisabledDeleteEnabled();

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<WikiDbContext>(o => o.UseInMemoryDatabase(nameof(PipelineRecomposeTests)))
            .AddSingleton<IWikiJsClient, NoopWikiJsClient>()
            .AddSingleton<IWikiContentReader, NoopContentReader>()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddPlatformPipelineStep<DocumentSyncConsumer>(pipeline);
                cfg.AddPlatformPipelineStep<DocumentDeletedConsumer>(pipeline);
            })
            .BuildServiceProvider(true);

        // wiki-sync は登録されず、wiki-delete のみ登録される
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetService<DocumentSyncConsumer>().Should().BeNull();
            scope.ServiceProvider.GetService<DocumentDeletedConsumer>().Should().NotBeNull();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // DocumentDeleted は処理される（削除段は有効のまま）
        await harness.Bus.Publish(new DocumentDeleted(Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentDeleted>(TestContext.Current.CancellationToken)).Should().BeTrue();

        // DocumentUpdated は購読されない（同期段は構成で無効）
        await harness.Bus.Publish(new DocumentUpdated(
            Guid.NewGuid(), "組み替えテスト", "published", "s3://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<DocumentUpdated>(TestContext.Current.CancellationToken)).Should().BeFalse();

        await harness.Stop(TestContext.Current.CancellationToken);
    }
}

file sealed class NoopWikiJsClient : IWikiJsClient
{
    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;
    public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

file sealed class NoopContentReader : IWikiContentReader
{
    public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult($"# {title}");
}
