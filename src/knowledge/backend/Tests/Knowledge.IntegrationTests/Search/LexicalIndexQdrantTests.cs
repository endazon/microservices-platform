using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using RetrievalService.Common.Observability;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Infrastructure.ExternalServices;
using Testcontainers.Qdrant;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-03, FR-05, ADR-0127 決定 1・2, ADR-0092 決定 3, [[IADR-0497]] 決定 1・4・5 (#1746):
// **語彙索引（ベクトルを持たない専用のコレクション）を実 Qdrant で通す。**
//
// 単体試験（gRPC の記録・InMemory）では測れないものだけを測る:
//   - ベクトルの設定が空のコレクションを**本番の取り込みアダプタが作れる**こと、ベクトル無しの点を書けること
//   - **本番の検索アダプタが全文（`text` / `text_ngram`）で当てられ、ABAC のフィルタが効く**こと
//   - **ベクトル検索は拒まれる**（＝意味検索の系統を構造的に持たない）こと
//   - 文書単位の削除が語彙索引にも届くこと
//   - ハイブリッド検索の合成（本番の `HybridSearchService`）で、キーワード・ハイブリッドには現れ、意味検索には現れないこと
//
// 🔴 **Qdrant の版は配備と同じ v1.18.1 に固定する**（`deploy/docker-compose.yml`・`deploy/local/infra/qdrant.yaml`）。
// 既定の `QdrantBuilder()` は v1.13.4 を起こす（Testcontainers 4.12.0）。置き場所の可否は版で変わり得るので、
// 配備の版で測る（作業仕様書 §実測。v1.13.4 でも同じ結果であった）。
//
// `Category=Integration` を付ける（実コンテナを起こす。`integration.yml` が回収する。PR の緑はこれが通ったことを意味しない）。
[Trait("Category", "Integration")]
[Trait("TestKind", "Integration")]
public sealed class LexicalIndexQdrantTests : IAsyncLifetime
{
    private const string QdrantImage = "qdrant/qdrant:v1.18.1";
    private const int Dimensions = 8;
    private const string VectorCollection = "knowledge_chunks_lexical_it_vector";
    private const string LexicalCollection = "knowledge_chunks_lexical_it";

    private const string PresentTerm = "人事評価の手順";
    private const string AbsentTerm = "アンチグラビティ";

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

        _qdrant = new QdrantBuilder(QdrantImage).Build();
        await _qdrant.StartAsync();

        var uri = new Uri(_qdrant.GetGrpcConnectionString());
        _client = new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == Uri.UriSchemeHttps);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_qdrant is not null) await _qdrant.DisposeAsync();
    }

    // I-09: 語彙索引の点は全文で当たり、ペイロードを書いたとおりに復元でき、ベクトルを持たない。
    [Fact]
    public async Task LexicalChunk_IsFoundByKeyword_AndHasNoVector()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        var writer = await NewWriterAsync(ct);
        await writer.UpsertLexicalChunkAsync(chunkId, documentId, "人事の文書",
            $"本文には {PresentTerm} と ABAC-42 が含まれる。", 0, "storage://knowledge/hr.md",
            new Dictionary<string, string> { ["confidentiality"] = "restricted", ["department"] = "hr" },
            ["人事"], DateTimeOffset.UtcNow, ["bob"], ct);

        var reader = NewReader(LexicalCollection);
        var hits = await reader.KeywordSearchAsync(PresentTerm, 10, ScopeFilter.Empty, ct);
        hits.Should().Contain(r => r.DocumentId == documentId, "日本語の語（text_ngram）で当たる");
        (await reader.KeywordSearchAsync("ABAC-42", 10, ScopeFilter.Empty, ct))
            .Should().Contain(r => r.DocumentId == documentId, "識別子（text）で当たる");
        (await reader.KeywordSearchAsync(AbsentTerm, 10, ScopeFilter.Empty, ct))
            .Should().NotContain(r => r.DocumentId == documentId, "陰性対照: 無い語では当たらない");

        var hit = hits.First(r => r.DocumentId == documentId);
        hit.DocumentTitle.Should().Be("人事の文書");
        hit.Attributes.Should().Contain("confidentiality", "restricted");
        hit.Tags.Should().Contain("人事");

        var stored = await _client!.ScrollAsync(LexicalCollection, limit: 10,
            vectorsSelector: new Qdrant.Client.Grpc.WithVectorsSelector { Enable = true }, cancellationToken: ct);
        stored.Result.Should().ContainSingle(p => p.Id.Uuid == chunkId.ToString())
            .Which.Vectors.Vectors.Vectors.Should().BeEmpty("語彙索引の点はベクトルを持たない（ADR-0127 決定 1）");
    }

    // I-09: ABAC のフィルタは語彙索引の全文の系統にも効く（陽性対照つき）。
    [Fact]
    public async Task AbacFilter_BlocksUnauthorized_OnLexicalCollection()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();

        var writer = await NewWriterAsync(ct);
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), documentId, "人事の文書",
            $"本文には {PresentTerm} が含まれる。", 0, null,
            new Dictionary<string, string> { ["confidentiality"] = "confidential" }, [], ct: ct);

        var reader = NewReader(LexicalCollection);
        var denied = new ScopeFilter([new AttributeFilter("confidentiality", ["public", "internal"])]);
        var allowed = new ScopeFilter([new AttributeFilter("confidentiality", ["confidential"])]);

        (await reader.KeywordSearchAsync(PresentTerm, 10, denied, ct))
            .Should().NotContain(r => r.DocumentId == documentId, "権限外の高機密文書は出ない");
        (await reader.KeywordSearchAsync(PresentTerm, 10, allowed, ct))
            .Should().Contain(r => r.DocumentId == documentId, "陽性対照: 許すスコープなら出る");
    }

    // I-09: 語彙索引へのベクトル検索は Qdrant が拒む —— 意味検索の系統を構造的に持たない（検索側はそもそも引かない）。
    [Fact]
    public async Task VectorSearch_IsRejected_OnLexicalCollection()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        await NewWriterAsync(ct);

        var act = () => NewReader(LexicalCollection).SearchAsync(new float[Dimensions], 10, ScopeFilter.Empty, ct);

        await act.Should().ThrowAsync<RpcException>();
    }

    // I-09: 文書単位の削除（取り込みの「全コレクションから消す」）が語彙索引にも届く。
    [Fact]
    public async Task DeleteByDocumentFromAll_RemovesLexicalPoints()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();

        var writer = await NewWriterAsync(ct);
        await writer.UpsertLexicalMetadataPointAsync(Guid.NewGuid(), documentId, $"{PresentTerm} の資料",
            $"{PresentTerm} の資料", null, new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            [], ct: ct);
        var reader = NewReader(LexicalCollection);
        (await reader.KeywordSearchAsync(PresentTerm, 10, ScopeFilter.Empty, ct))
            .Should().Contain(r => r.DocumentId == documentId, "陽性対照: 消す前は当たる");

        await writer.DeleteByDocumentFromAllAsync(documentId, ct);

        (await reader.KeywordSearchAsync(PresentTerm, 10, ScopeFilter.Empty, ct))
            .Should().NotContain(r => r.DocumentId == documentId);
    }

    // I-10: 本番の合成（`HybridSearchService` ＋ 語彙索引を `LexicalOnly` で束ねる）を実 Qdrant で通す。
    // 高機密文書はキーワード・ハイブリッドで現れ、意味検索では現れない。公開文書は意味検索に現れる（陽性対照）。
    [Fact]
    public async Task HybridSearch_IncludesLexical_ForKeywordAndHybrid_ButNotSemantic()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var secret = Guid.NewGuid();
        var open = Guid.NewGuid();
        var queryVector = Vectorize(PresentTerm);

        var writer = await NewWriterAsync(ct);
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), secret, "人事の文書",
            $"本文には {PresentTerm} が含まれる。", 0, null,
            new Dictionary<string, string> { ["confidentiality"] = "restricted" }, [], ct: ct);
        await writer.UpsertChunkAsync(VectorCollection, Guid.NewGuid(), open, "公開の文書",
            $"公開の本文にも {PresentTerm} が含まれる。", 0, queryVector, null,
            new Dictionary<string, string> { ["confidentiality"] = "public" }, [], ct: ct);

        var service = new HybridSearchService(NewReader(VectorCollection), new FixedQueryEmbedding(queryVector),
            NullLogger<HybridSearchService>.Instance,
            new FusedCollections([new FusedCollection(LexicalCollection, NewReader(LexicalCollection),
                NoQueryEmbedding.Instance, LexicalOnly: true)]));

        async Task<List<SearchResultDto>> Search(string mode) =>
            await service.SearchAsync(new SearchRequest(PresentTerm, 10, null, new AccessScope([], true), mode),
                new SearchUserContext("it-user", new Dictionary<string, string>(), true, null), ct);

        (await Search(SearchModes.Keyword)).Should().Contain(r => r.DocumentId == secret);
        (await Search(SearchModes.Hybrid)).Should().Contain(r => r.DocumentId == secret);
        var semantic = await Search(SearchModes.Semantic);
        semantic.Should().Contain(r => r.DocumentId == open, "陽性対照: 意味検索は公開の文書を返す");
        semantic.Should().NotContain(r => r.DocumentId == secret, "高機密文書は意味検索に現れない（ADR-0127 決定 2）");
    }

    // I-09（監査 🟡3, [[IADR-0497]] 決定 4）: 語彙索引のコレクションが**まだ無い**とき（ブートストラップ前・失敗）。
    //   - 実 Qdrant は無いコレクションの削除を `NotFound` で返す（検索側の no-op が前提とする状態コード。
    //     検索側の分岐そのものは `LexicalCollectionDeleteTests` が同じ状態コードで測る）。
    //   - 取り込みの「全コレクションから消す」は、語彙索引が無くてもベクトルのコレクションから消して成功する。
    [Fact]
    public async Task MissingLexicalCollection_DeleteIsNotFound_AndIngestionDeleteStillSucceeds()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var missing = $"knowledge_chunks_lexical_missing_{Guid.NewGuid():N}";
        var documentId = Guid.NewGuid();

        var act = () => NewReader(missing).DeleteByDocumentAsync(documentId, ct);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound,
            "主（既定）の削除は無いコレクションで従来どおり例外を上げる。語彙索引はこの NotFound だけを no-op にする");

        // ベクトルのコレクションを作り、点を 1 つ置く。語彙索引の名前は存在しないものにする（ブートストラップしない）。
        var vectorOnly = new QdrantIngestionVectorStore(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections = [new EmbeddingCollectionOptions { Name = VectorCollection, VectorSize = Dimensions }]
        }), missing);
        await NewWriterAsync(ct);   // VectorCollection を作る（語彙索引 LexicalCollection も作られるが、上の writer は missing を見る）
        await vectorOnly.UpsertChunkAsync(VectorCollection, Guid.NewGuid(), documentId, "公開の文書",
            $"{PresentTerm} を含む", 0, Vectorize(PresentTerm), null,
            new Dictionary<string, string> { ["confidentiality"] = "public" }, [], ct: ct);

        await vectorOnly.DeleteByDocumentFromAllAsync(documentId, ct);

        (await NewReader(VectorCollection).KeywordSearchAsync(PresentTerm, 10, ScopeFilter.Empty, ct))
            .Should().NotContain(r => r.DocumentId == documentId, "ベクトルのコレクションからは消えている");
    }

    // ── 器 ────────────────────────────────────────────────

    private async Task<QdrantIngestionVectorStore> NewWriterAsync(CancellationToken ct)
    {
        var writer = new QdrantIngestionVectorStore(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections = [new EmbeddingCollectionOptions { Name = VectorCollection, VectorSize = Dimensions }]
        }), LexicalCollection);
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        return writer;
    }

    private QdrantVectorStore NewReader(string collection) =>
        new(_client!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = collection
            }).Build(),
            NullLogger<QdrantVectorStore>.Instance,
            new KeywordSearchMetrics(new QdrantTestMeterFactory()));

    private static float[] Vectorize(string text) => DeterministicEmbeddingService.Vectorize(text);

    private sealed class FixedQueryEmbedding(float[] vector) : RetrievalService.Domain.Ports.IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(vector);
    }
}
