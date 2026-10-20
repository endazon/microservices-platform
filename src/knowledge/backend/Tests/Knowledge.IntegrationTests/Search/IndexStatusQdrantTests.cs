using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Testcontainers.Qdrant;

namespace Knowledge.IntegrationTests.Search;

// FR-06, FR-02, SC-05, ADR-0127, [[IADR-0536]], [[IADR-0565]] 決定 1・2 (#1993):
// **索引の状態の材料（どの文書が点を持つか）を、実 Qdrant の facet で引く。**
//
// 単体試験（`QdrantIndexPresenceTests`。gRPC の記録）では測れないものだけを測る:
//   - ブートストラップが張る `document_id` のキーワード索引で、facet が例外にならないこと（索引の無いキーは facet できない）
//   - 本番の書き込みの口（主・語彙索引〔印なし〕・埋め込み待ち〔印あり〕）で書いた文書が 3 値に分かれること
//   - フィルタが渡した ID に絞り、ページの外の文書を数えないこと
//
// 🔴 **Qdrant の版は配備と同じ v1.18.1 に固定する**（置き場所は `QdrantTestImage`。#1790）。
[Trait("Category", "Integration")]
[Trait("TestKind", "Integration")]
public sealed class IndexStatusQdrantTests : IAsyncLifetime
{
    private const int Dimensions = 8;

    private readonly string _primary = $"is_it_primary_{Guid.NewGuid():N}";
    private readonly string _lexical = $"is_it_lexical_{Guid.NewGuid():N}";

    private QdrantContainer? _qdrant;
    private QdrantClient? _client;

    public async ValueTask InitializeAsync()
    {
        var external = RequiredServices.Qdrant.External;
        if (external is not null)
        {
            var parts = external.Split(':', 2);
            _client = new QdrantClient(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6334);
            return;
        }

        if (!DockerRequired.IsAvailable()) return;

        _qdrant = QdrantTestImage.CreateBuilder().Build();
        await _qdrant.StartAsync();

        var uri = new Uri(_qdrant.GetGrpcConnectionString());
        _client = new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == Uri.UriSchemeHttps);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_qdrant is not null) await _qdrant.DisposeAsync();
    }

    // I-12: 主に書いた文書は索引済み、語彙索引（印なし。高機密）だけの文書も索引済み、埋め込み待ちの点を持つ文書は埋め込み待ち、
    // 一部だけ埋め込めた文書（主と印ありの両方）は埋め込み待ち、どこにも無い文書は未索引。ページの外の文書は答えに出ない。
    [Fact]
    public async Task 書いた点から3値に分かれ_ページの外の文書を数えない()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var writer = await NewWriterAsync(ct);

        var embedded = Guid.NewGuid();
        var lexicalOnly = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var partial = Guid.NewGuid();
        var absent = Guid.NewGuid();
        var outsidePage = Guid.NewGuid();
        var attrs = new Dictionary<string, string> { ["confidentiality"] = "internal" };

        // 1 文書に複数のチャンク（facet の数は捨て、有無だけを使う）。
        await writer.UpsertChunkAsync(_primary, Guid.NewGuid(), embedded, "主", "本文 1", 0, Vector(), null, new(attrs), [], ct: ct);
        await writer.UpsertChunkAsync(_primary, Guid.NewGuid(), embedded, "主", "本文 2", 1, Vector(), null, new(attrs), [], ct: ct);
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), lexicalOnly, "高機密", "本文", 0, null,
            new() { ["confidentiality"] = "restricted" }, [], ct: ct);
        await writer.UpsertEmbeddingPendingChunkAsync(Guid.NewGuid(), pending, "待ち", "本文", 0, null,
            new(attrs), [], null, null, EmbeddingPendingReasons.Transient, ct);
        await writer.UpsertChunkAsync(_primary, Guid.NewGuid(), partial, "一部", "本文 1", 0, Vector(), null, new(attrs), [], ct: ct);
        await writer.UpsertEmbeddingPendingChunkAsync(Guid.NewGuid(), partial, "一部", "本文 2", 1, null,
            new(attrs), [], null, null, EmbeddingPendingReasons.Transient, ct);
        await writer.UpsertChunkAsync(_primary, Guid.NewGuid(), outsidePage, "外", "本文", 0, Vector(), null, new(attrs), [], ct: ct);

        var page = new[] { embedded, lexicalOnly, pending, partial, absent };
        var presence = await writer.GetIndexPresenceAsync(page, ct);

        DocumentIndexStatusRules.Classify(embedded, presence).Should().Be(IndexState.Indexed);
        DocumentIndexStatusRules.Classify(lexicalOnly, presence).Should().Be(IndexState.Indexed);
        DocumentIndexStatusRules.Classify(pending, presence).Should().Be(IndexState.EmbeddingPending);
        DocumentIndexStatusRules.Classify(partial, presence).Should().Be(IndexState.EmbeddingPending);
        DocumentIndexStatusRules.Classify(absent, presence).Should().Be(IndexState.NotIndexed);
        presence.Indexed.Concat(presence.EmbeddingPending).Should().NotContain(outsidePage, "ページの外の文書は数えない");
    }

    // 陰性対照: `document_id` の索引が無いと facet は失敗する（＝ブートストラップで張る根拠）。
    [Fact]
    public async Task document_idの索引が無ければfacetは失敗する()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var writer = NewWriter();
        await writer.EnsureCollectionsAsync(ct); // 索引を張らない

        var act = () => writer.GetIndexPresenceAsync([Guid.NewGuid()], ct);

        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    private QdrantIngestionVectorStore NewWriter() =>
        new(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections = [new EmbeddingCollectionOptions { Name = _primary, VectorSize = Dimensions }],
        }), _lexical, NullLogger<QdrantIngestionVectorStore>.Instance);

    // 起動時のブートストラップと同じ順に呼ぶ。
    private async Task<QdrantIngestionVectorStore> NewWriterAsync(CancellationToken ct)
    {
        var writer = NewWriter();
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        await writer.EnsureKeywordIndexesAsync(ct);
        await writer.EnsureDocumentIdIndexAsync(ct);
        return writer;
    }

    private static float[] Vector() => DeterministicEmbeddingService.Vectorize("index-status-it");
}
