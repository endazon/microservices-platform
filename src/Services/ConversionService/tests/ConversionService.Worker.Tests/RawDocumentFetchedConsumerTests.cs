using ConversionService.Worker.Composable.Steps;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Services;
using ConversionService.Worker.Foundation.Domain;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06: ConversionService Consumer ユニットテスト
public class RawDocumentFetchedConsumerTests
{
    [Fact]
    public async Task Consumer_publishes_DocumentNormalized_with_deterministic_id_and_assets()
    {
        var sourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var normalizer = new FakeNormalizationService(new NormalizationResult(
            DocumentId: DeterministicGuid.ForDocument(sourceId, "/docs/test.docx"),
            MarkdownUri: "storage://normalized/doc.md",
            AssetUris: ["storage://normalized/assets/fig-1.png"],
            DiagramsCoded: 1,
            DiagramsRetained: 1));

        await using var provider = new ServiceCollection()
            .AddSingleton<INormalizationService>(normalizer)
            // SC-07: コンシューマは変換ジョブストアに依存する（状況記録）。
            .AddSingleton<IConversionJobStore, InMemoryConversionJobStore>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RawDocumentFetchedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new RawDocumentFetched(
            Guid.NewGuid(), sourceId, "filesystem", "/docs/test.docx",
            "storage://bucket/raw/test.docx", "application/msword",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["knowledge-mgmt"],
            DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
        (await harness.Published.Any<DocumentNormalized>()).Should().BeTrue();

        var published = harness.Published.Select<DocumentNormalized>().First().Context.Message;
        published.DocumentId.Should().Be(DeterministicGuid.ForDocument(sourceId, "/docs/test.docx"));
        published.MarkdownUri.Should().Be("storage://normalized/doc.md");
        published.AssetUris.Should().ContainSingle().Which.Should().Contain("fig-1.png");
        published.Title.Should().Be("test");

        await harness.Stop();
    }

    private sealed class FakeNormalizationService(NormalizationResult result) : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw,
            CancellationToken ct = default) => Task.FromResult(result);
    }
}
