using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 2 (i)・3 (#1760): 起動時の器がキーワード索引の 2 つの経路を呼ぶこと。
//   - ブートストラップ（`QdrantBootstrapHostedService`）が集合値キーの索引を張る（全文索引の後）。
//   - 発見（`QdrantKeywordIndexDiscoveryHostedService`）が既存の点の属性キーへ張り、失敗してもホストを止めない。
[Trait("TestKind", "Unit")]
public class QdrantKeywordIndexHostedServiceTests
{
    // T-39: ブートストラップは全文索引に続けてキーワード索引を張る。
    [Fact]
    public async Task Bootstrap_EnsuresKeywordIndexesAfterFullTextIndexes()
    {
        var store = new RecordingStore();
        var bootstrap = new QdrantBootstrapHostedService(Provider(store),
            NullLogger<QdrantBootstrapHostedService>.Instance);

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);

        store.Calls.Should().Equal(["collections", "cjk", "keyword"]);
    }

    // T-39: 発見の器は既存の点からの発見を呼ぶ。失敗はここで捕まえる（ホストを止めない）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_RunsInBackground_AndSwallowsFailures(bool fail)
    {
        var store = new RecordingStore { FailDiscovery = fail };
        var discovery = new QdrantKeywordIndexDiscoveryHostedService(Provider(store),
            NullLogger<QdrantKeywordIndexDiscoveryHostedService>.Instance);

        await discovery.StartAsync(TestContext.Current.CancellationToken);
        await (discovery.ExecuteTask ?? Task.CompletedTask);

        store.Calls.Should().Equal(["discovery"]);
        discovery.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("例外でホストを止めない");
    }

    private static IServiceProvider Provider(IIngestionVectorStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    private sealed class RecordingStore : IIngestionVectorStore
    {
        internal List<string> Calls { get; } = [];
        internal bool FailDiscovery { get; init; }

        public Task EnsureCollectionsAsync(CancellationToken ct = default)
        {
            Calls.Add("collections");
            return Task.CompletedTask;
        }

        public Task EnsureCjkNgramIndexAsync(CancellationToken ct = default)
        {
            Calls.Add("cjk");
            return Task.CompletedTask;
        }

        public Task EnsureKeywordIndexesAsync(CancellationToken ct = default)
        {
            Calls.Add("keyword");
            return Task.CompletedTask;
        }

        public Task<int> EnsureKeywordIndexesForExistingPointsAsync(CancellationToken ct = default)
        {
            Calls.Add("discovery");
            return FailDiscovery
                ? Task.FromException<int>(new InvalidOperationException("simulated"))
                : Task.FromResult(1);
        }

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
