using FluentAssertions;
using IngestionService.Worker.Consumers;
using IngestionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionService.Worker.Tests;

// FR-02, UC-04: IngestionService 取り込みパイプライン（parse→chunk→embed→index）テスト
public class DocumentUpdatedConsumerTests
{
    private static DocumentUpdated SampleEvent(string? markdownUri = "https://storage.example/docs/test.md")
        => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "テスト文書",
            "normalized",
            markdownUri,
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["knowledge-mgmt", "ops"],
            DateTimeOffset.UtcNow);

    private static ServiceProvider BuildHarness(
        RecordingVectorStore store,
        IDocumentContentReader reader)
        => new ServiceCollection()
            .AddSingleton<IChunkingService, MarkdownChunkingService>()
            .AddSingleton<IEmbeddingService, StubEmbeddingService>()
            .AddSingleton<IDocumentContentReader>(reader)
            .AddSingleton<IIngestionVectorStore>(store)
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<DocumentUpdatedConsumer>())
            .BuildServiceProvider(true);

    // T-01 / T-02: パイプラインが消費し、パース本文由来のチャンクが索引登録される
    [Fact]
    public async Task Consumer_ShouldParseChunkEmbedAndIndex()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# 見出しA\n\n本文アルファ\n\n# 見出しB\n\n本文ベータ");
        await using var provider = BuildHarness(store, reader);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(SampleEvent());

            (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
            // 見出し 2 つ → 2 チャンク。パース本文の内容がチャンクに反映される。
            store.Upserts.Should().HaveCount(2);
            store.Upserts.Select(u => u.Text).Should().Contain(t => t.Contains("本文アルファ"));
        }
        finally { await harness.Stop(); }
    }

    // T-03: ペイロードに chunk_index / tags / attributes が保持される
    [Fact]
    public async Task Consumer_ShouldPreserveChunkIndexTagsAndAttributes()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");
        await using var provider = BuildHarness(store, reader);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(SampleEvent());
            (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();

            store.Upserts.Select(u => u.ChunkIndex).Should().BeEquivalentTo(new[] { 0, 1 });
            store.Upserts.Should().OnlyContain(u => u.Tags.Contains("knowledge-mgmt"));
            store.Upserts.Should().OnlyContain(u => u.Attributes["confidentiality"] == "internal");
        }
        finally { await harness.Stop(); }
    }

    // T-04: 決定的チャンク ID により再取り込みで ID が一致する（冪等）
    [Fact]
    public async Task Consumer_ShouldUseDeterministicChunkIds_AcrossReingestion()
    {
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");

        async Task<List<Guid>> IngestOnce()
        {
            var store = new RecordingVectorStore();
            await using var provider = BuildHarness(store, reader);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();
            try
            {
                await harness.Bus.Publish(SampleEvent());
                (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
                return store.Upserts.Select(u => u.ChunkId).ToList();
            }
            finally { await harness.Stop(); }
        }

        var first = await IngestOnce();
        var second = await IngestOnce();
        first.Should().Equal(second);
    }

    // T-06: 取り込み完了で IngestionCompleted が発行される
    [Fact]
    public async Task Consumer_ShouldPublishIngestionCompleted()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる");
        await using var provider = BuildHarness(store, reader);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(SampleEvent());
            (await harness.Published.Any<IngestionCompleted>()).Should().BeTrue();
        }
        finally { await harness.Stop(); }
    }

    // T-05: MarkdownUri が null なら登録 0 件で正常終了する（例外フロー E1）
    [Fact]
    public async Task Consumer_ShouldSkip_WhenMarkdownUriIsNull()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("ignored");
        await using var provider = BuildHarness(store, reader);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(SampleEvent(markdownUri: null));
            (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();
            store.Upserts.Should().BeEmpty();
        }
        finally { await harness.Stop(); }
    }
}

// テスト用スタブ群
file class StubEmbeddingService : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[1536]);
}

file class StubContentReader(string markdown) : IDocumentContentReader
{
    public Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult(markdown);
}

file record UpsertRecord(Guid ChunkId, Guid DocumentId, string Title, string Text,
    int ChunkIndex, string? MarkdownUri, Dictionary<string, string> Attributes, List<string> Tags);

file class RecordingVectorStore : IIngestionVectorStore
{
    public List<UpsertRecord> Upserts { get; } = [];
    public bool CollectionEnsured { get; private set; }

    public Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        CollectionEnsured = true;
        return Task.CompletedTask;
    }

    public Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags, CancellationToken ct = default)
    {
        Upserts.Add(new UpsertRecord(chunkId, documentId, title, text, chunkIndex,
            markdownUri, attributes, tags));
        return Task.CompletedTask;
    }

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
        => Task.CompletedTask;
}
