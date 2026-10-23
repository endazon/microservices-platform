using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Testcontainers.Qdrant;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-05, [[IADR-0595]] 決定 1・2 (#2101): **版の印と旧版の点の削除を実 Qdrant で通す。**
//
// 単体試験（gRPC の記録）では測れないものだけを測る:
//   - 旧版の削除のフィルタ（`updated_at` の範囲の must_not と ID の除外）を Qdrant が意図どおりに解釈すること ——
//     `updated_at` を持たない点は消え、より新しい版の点と書いた点は残り、同じ ID でも別のコレクションの点は消える
//   - 版の印のコレクションが最初の書き込みで作られ、印を読み戻せること（ベクトルを持たない点）
//
// 🔴 **Qdrant の版は配備と同じ v1.18.1 に固定する**（`QdrantTestImage`）。
// `Category=Integration` を付ける（実コンテナを起こす。`integration.yml` が回収する）。
[Trait("Category", "Integration")]
[Trait("TestKind", "Integration")]
public sealed class IngestionIndexVersionQdrantTests : IAsyncLifetime
{
    private const int Dimensions = 4;
    private const string VectorCollection = "knowledge_chunks_version_it_vector";
    private const string LexicalCollection = "knowledge_chunks_version_it_lexical";

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

    // I-2101-1: 旧版の削除は「文書の点 ∧ ¬(updated_at > 版) ∧ ¬(書いた点)」を消す。陽性対照（残る点）と陰性（消える点）を同じ文書に並べる。
    [Fact]
    public async Task 旧版の削除は古い点と版を持たない点を消し_新しい版と書いた点を残す()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        var doc = Guid.NewGuid();
        var other = Guid.NewGuid();
        var v1 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var v2 = v1.AddMinutes(1);
        var v3 = v2.AddMinutes(1);

        var written = Guid.NewGuid();      // この取り込み（v2）で書いた点（語彙索引）
        var sameIdInVector = written;      // 同じ ID の点がベクトルのコレクションにも残っている（置き場所の移動前）
        var olderLeftover = Guid.NewGuid(); // v1 の余ったチャンク
        var legacy = Guid.NewGuid();        // `updated_at` を持たない移行前の点
        var sameVersionUnwritten = Guid.NewGuid(); // v2 だが今回書いていない点
        var newer = Guid.NewGuid();         // 並行して反映された v3 の点
        var otherDoc = Guid.NewGuid();      // 別の文書の点

        await store.UpsertLexicalChunkAsync(written, doc, "t", "a", 0, null, Attrs(), [], v2, ct: ct);
        await store.UpsertChunkAsync(VectorCollection, sameIdInVector, doc, "t", "a", 0, Vec(), null, Attrs(), [], v1, ct: ct);
        await store.UpsertLexicalChunkAsync(olderLeftover, doc, "t", "b", 1, null, Attrs(), [], v1, ct: ct);
        await store.UpsertLexicalChunkAsync(legacy, doc, "t", "c", 2, null, Attrs(), [], updatedAt: null, ct: ct);
        await store.UpsertLexicalChunkAsync(sameVersionUnwritten, doc, "t", "d", 3, null, Attrs(), [], v2, ct: ct);
        await store.UpsertChunkAsync(VectorCollection, newer, doc, "t", "e", 4, Vec(), null, Attrs(), [], v3, ct: ct);
        await store.UpsertLexicalChunkAsync(otherDoc, other, "t", "f", 0, null, Attrs(), [], v1, ct: ct);

        await store.DeleteSupersededAsync(doc, v2, [IndexedPointRef.Lexical(written)], ct);

        (await IdsIn(LexicalCollection, ct)).Should().BeEquivalentTo([written.ToString(), otherDoc.ToString()],
            "語彙索引には書いた点と別の文書の点だけが残る（古い余り・版を持たない点・書いていない同版の点は消える）");
        (await IdsIn(VectorCollection, ct)).Should().BeEquivalentTo([newer.ToString()],
            "ベクトルのコレクションには新しい版の点だけが残る（同じ ID でも別のコレクションの古い点は消える）");
    }

    // I-2101-2: 版の印のコレクションは最初の書き込みで作られ、印を読み戻せる。書く前は「印が無い」と読む。
    [Fact]
    public async Task 版の印は最初の書き込みでコレクションごと作られ読み戻せる()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        var doc = Guid.NewGuid();
        var version = new DocumentIndexVersion(new DateTimeOffset(2026, 10, 23, 9, 30, 15, 123, TimeSpan.Zero), "fp");

        (await store.GetIndexVersionAsync(doc, ct)).Should().BeNull();

        await store.SetIndexVersionAsync(doc, version, ct);
        (await store.GetIndexVersionAsync(doc, ct)).Should().Be(version);

        var later = version with { UpdatedAt = version.UpdatedAt.AddSeconds(1), AccessFingerprint = "" };
        await store.SetIndexVersionAsync(doc, later, ct);
        (await store.GetIndexVersionAsync(doc, ct)).Should().Be(later, "1 文書 1 点で上書きされる");
    }

    private async Task<QdrantIngestionVectorStore> NewStoreAsync(CancellationToken ct)
    {
        var store = new QdrantIngestionVectorStore(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections = [new EmbeddingCollectionOptions { Name = VectorCollection, VectorSize = Dimensions }]
        }), LexicalCollection);
        await store.EnsureCollectionsAsync(ct);
        await store.EnsureDocumentIdIndexAsync(ct);
        return store;
    }

    private async Task<List<string>> IdsIn(string collection, CancellationToken ct)
    {
        var page = await _client!.ScrollAsync(collection, limit: 100, cancellationToken: ct);
        return [.. page.Result.Select(p => p.Id.Uuid)];
    }

    private static Dictionary<string, string> Attrs() => new() { ["confidentiality"] = "internal" };

    private static float[] Vec() => [0.1f, 0.2f, 0.3f, 0.4f];
}
