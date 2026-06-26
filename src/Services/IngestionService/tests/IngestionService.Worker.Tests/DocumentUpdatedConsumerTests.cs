using FluentAssertions;
using IngestionService.Worker.Consumers;
using IngestionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionService.Worker.Tests;

// FR-02, UC-04: IngestionService Consumer ユニットテスト
public class DocumentUpdatedConsumerTests
{
    [Fact]
    public async Task Consumer_ShouldConsumeDocumentUpdatedMessage()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IChunkingService, MarkdownChunkingService>()
            .AddSingleton<IEmbeddingService, StubEmbeddingService>()
            .AddSingleton<IIngestionVectorStore, InMemoryIngestionVectorStore>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentUpdatedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new DocumentUpdated(
            Guid.NewGuid(), "テスト文書", "normalized",
            "storage://bucket/docs/test.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["knowledge-mgmt"],
            DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
        await harness.Stop();
    }
}

// テスト用スタブ
file class StubEmbeddingService : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[1536]);
}

file class InMemoryIngestionVectorStore : IIngestionVectorStore
{
    public Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
        => Task.CompletedTask;
}
