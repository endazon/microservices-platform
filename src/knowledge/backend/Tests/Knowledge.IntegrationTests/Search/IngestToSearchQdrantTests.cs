using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using RetrievalService.Common.Observability;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;
using Testcontainers.Qdrant;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-03, ADR-0009, ADR-0016, [[IADR-0390]] (#1247):
// **取り込み → 索引 → 検索**を**実 Qdrant** で通す段間結合テスト（層 2）。
//
// ## 層 1 では原理的に測れないものだけを測る
//
// `IngestToSearchInProcessTests` は書き手と読み手を**自分で書いた橋**で繋ぐので、
// 2 つの Qdrant アダプタ（`QdrantIngestionVectorStore` / `QdrantVectorStore`）の
// **ペイロード表現が一致しているか**は測れない。両者はコレクション名とペイロード鍵
// （`text` / `document_id` / `attributes.*`）を**文字列で**合わせており、サービスを跨ぐため
// 型では束ねられない —— [[IADR-0014]] が実際に踏んだ「テストは緑・本番は空」の型である。
// 稼働 dev クラスタで検索が全件 0 件になった事故（#1215）の一因も読み書き先の不一致だった。
//
// 🔴 **`Category=Integration` を付ける。** 実コンテナを起こすので `ci.yml` の
// `--filter "Category!=Integration"` で PR からは外れ、`integration.yml`（push: develop ＋ 日次 cron）が
// 回収する（[[IADR-0232]] 決定 3）。**PR の緑はこのテストが通ったことを意味しない。**
[Trait("Category", "Integration")]
[Trait("TestKind", "Integration")]
public sealed class IngestToSearchQdrantTests : IAsyncLifetime
{
    // 8 次元。実モデル（1024 / 768）に合わせる必要はない —— 測るのは**表現の一致**であって
    // 埋め込みの品質ではない。次元を小さくするとコンテナの起動と索引作成が速い。
    private const int Dimensions = 8;
    private const string Collection = "knowledge_chunks_ingest_to_search";

    private const string PresentTerm = "ホログラフィック索引";
    private const string AbsentTerm = "アンチグラビティ";

    private QdrantContainer? _qdrant;
    private QdrantClient? _client;

    public async ValueTask InitializeAsync()
    {
        if (!DockerRequired.IsAvailable()) return;

        _qdrant = new QdrantBuilder().Build();
        await _qdrant.StartAsync();

        var uri = new Uri(_qdrant.GetGrpcConnectionString());
        _client = new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == Uri.UriSchemeHttps);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_qdrant is not null) await _qdrant.DisposeAsync();
    }

    // 🔴 **本試験の中心。0 件で緑にならない。**
    // 書き込みは本番の `QdrantIngestionVectorStore`、読み出しは本番の `QdrantVectorStore` である。
    // どちらかを差し替えたら、このテストは測りたいものを測っていない。
    [Fact]
    public async Task ChunkWrittenByIngestion_IsFoundByRetrieval()
    {
        DockerRequired.SkipUnlessAvailable();

        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var text = $"本文には {PresentTerm} という語が含まれる。";

        // ── 段 5: 取り込み側の本番アダプタで書く
        var writer = NewIngestionStore();
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        await writer.UpsertChunkAsync(Collection, chunkId, documentId, "段間結合テストの文書",
            text, chunkIndex: 0, vector: Vectorize(text), markdownUri: "storage://knowledge/x.md",
            attributes: new Dictionary<string, string> { ["confidentiality"] = "public" },
            tags: ["段間結合"], updatedAt: DateTimeOffset.UtcNow, ct: ct);

        // ── 段 6: 検索側の本番アダプタで読む
        var reader = NewRetrievalStore();
        var scope = ScopeFilter.Empty;

        var keyword = await reader.KeywordSearchAsync(PresentTerm, 10, scope, ct);
        keyword.Should().Contain(r => r.DocumentId == documentId,
            "取り込みが書いた点が全文検索で当たること。"
            + "0 件なら、書き込み側と読み出し側のコレクション名かペイロード鍵がずれている");

        var semantic = await reader.SearchAsync(Vectorize(text), 10, scope, ct);
        semantic.Should().Contain(r => r.DocumentId == documentId,
            "同じ点が意味検索でも当たること（ベクトルの書き込み先が読み出し先と同じであること）");

        // 復元したペイロードが**書いた値と同じ**であること —— ここがずれると、
        // 検索は当たるのに表示（題名・出典・タグ・属性）が空になる。
        var hit = keyword.First(r => r.DocumentId == documentId);
        hit.DocumentTitle.Should().Be("段間結合テストの文書");
        hit.Text.Should().Contain(PresentTerm);
        hit.MarkdownUri.Should().Be("storage://knowledge/x.md");
        hit.Tags.Should().Contain("段間結合");
        hit.Attributes.Should().ContainKey("confidentiality");
    }

    // 陰性対照。**上と対で置く** —— 「何を検索しても当たる」実装でも上は緑になる。
    [Fact]
    public async Task UnrelatedTerm_DoesNotHit()
    {
        DockerRequired.SkipUnlessAvailable();

        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var text = $"本文には {PresentTerm} という語が含まれる。";

        var writer = NewIngestionStore();
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        await writer.UpsertChunkAsync(Collection, Guid.NewGuid(), documentId, "段間結合テストの文書",
            text, 0, Vectorize(text), null, [], [], DateTimeOffset.UtcNow, ct);

        var reader = NewRetrievalStore();
        var scope = ScopeFilter.Empty;

        // 陽性対照（走査が生きていること）。
        (await reader.KeywordSearchAsync(PresentTerm, 10, scope, ct))
            .Should().Contain(r => r.DocumentId == documentId, "陽性対照: 本文にある語では当たること");

        (await reader.KeywordSearchAsync(AbsentTerm, 10, scope, ct))
            .Should().NotContain(r => r.DocumentId == documentId,
                $"本文に無い語（{AbsentTerm}）では当たらないこと");
    }

    // FR-02, FR-05: 取り込みが消したものは検索に残らない（残ると機密区分変更が ABAC を跨ぐ）。
    [Fact]
    public async Task DeletedDocument_DisappearsFromSearch()
    {
        DockerRequired.SkipUnlessAvailable();

        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var text = $"本文には {PresentTerm} という語が含まれる。";

        var writer = NewIngestionStore();
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        await writer.UpsertChunkAsync(Collection, Guid.NewGuid(), documentId, "段間結合テストの文書",
            text, 0, Vectorize(text), null, [], [], DateTimeOffset.UtcNow, ct);

        var reader = NewRetrievalStore();
        var scope = ScopeFilter.Empty;
        (await reader.KeywordSearchAsync(PresentTerm, 10, scope, ct))
            .Should().Contain(r => r.DocumentId == documentId, "陽性対照: 消す前は当たること");

        await writer.DeleteByDocumentFromAllAsync(documentId, ct);

        (await reader.KeywordSearchAsync(PresentTerm, 10, scope, ct))
            .Should().NotContain(r => r.DocumentId == documentId,
                "取り込みが削除した文書は検索に残らないこと");
    }

    // ── 器 ────────────────────────────────────────────────

    private QdrantIngestionVectorStore NewIngestionStore() =>
        new(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections = [new EmbeddingCollectionOptions { Name = Collection, VectorSize = Dimensions }]
        }));

    // 🔴 **`Qdrant:CollectionName` を書き込み先と同じ値にする。** ここを既定
    // （`knowledge_chunks`）のままにすると、書いた先と読む先が別のコレクションになり、
    // **本テストが再現しようとしている事故（#1215）そのものを自分で踏む。**
    private QdrantVectorStore NewRetrievalStore() =>
        new(_client!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = Collection
            }).Build(),
            NullLogger<QdrantVectorStore>.Instance,
            new KeywordSearchMetrics(new QdrantTestMeterFactory()));

    // 層 1 と同じ写像（`DeterministicEmbeddingService.Vectorize`）。
    private static float[] Vectorize(string text) => DeterministicEmbeddingService.Vectorize(text);
}

// `KeywordSearchMetrics` は `IMeterFactory` を要求する（Web ホストでは既定で入る）。
// ここはホストを立てないので器だけ用意する（`RetrievalService.Tests.TestMeterFactory` と同型。
// 別アセンブリの internal なので複写している）。
internal sealed class QdrantTestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter($"{options.Name}.test-{Guid.NewGuid():N}", options.Version,
            options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var m in _meters) m.Dispose();
        _meters.Clear();
    }
}
