using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.AttributeValues;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Features.Search.RemoveDeleted;
using RetrievalService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Events;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-03, FR-05, UC-01, ADR-0016, ADR-0092 決定 1・2・3, [[IADR-0467]] (#336):
// **分離したコレクションを 1 回の検索で束ねる**ことを固定する。
//
// 🔴 3 つの守りを分けて測る。
//   (1) **不変**: 追加コレクションが空（既定）なら、戻り値・`Score`・呼び出し回数が従来と同一である
//   (2) **順位で束ねる**: 生スコアを比べる実装（変異 M-2）はここで赤になる
//   (3) **ABAC は全コレクションの全系統に掛かる**: 追加側のフィルタを外す実装（変異 M-1）はここで赤になる
[Trait("TestKind", "Unit")]
public class MultiCollectionFusionTests
{
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private static readonly AccessScope Granted = new([], true);

    private static SearchResultDto Hit(Guid id, float score) =>
        new(id, Guid.NewGuid(), "title", "text", score, "uri", new(), [], null);

    private static HybridSearchService Service(
        IVectorStore primary, IEmbeddingService primaryEmbed, params FusedCollection[] fused) =>
        new(primary, primaryEmbed, NullLogger<HybridSearchService>.Instance,
            fused.Length == 0 ? FusedCollections.None : new FusedCollections(fused));

    private static Task<List<SearchResultDto>> Search(
        HybridSearchService svc, string? mode, AccessScope? scope = null) =>
        svc.SearchAsync(new SearchRequest("問い", 10, null, scope ?? Granted, mode),
            TestSearchUser.Any, TestContext.Current.CancellationToken);

    // ---- (1) 不変: 追加コレクションが空なら従来と同一 ------------------------------------

    // T-F-01: hybrid。**`Score` は `RRF(主ベクトル, 主全文)` の値そのもの**で、並びは従来の同点規則
    // （先に現れた方が先）どおり。埋め込み 1 回・ベクトル 1 回・全文 1 回。
    [Fact]
    public async Task 追加が空ならhybridは従来のRRFと同じ値と並びを返す()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();
        var store = new ScriptedStore
        {
            Vector = [Hit(a, 0.9f), Hit(b, 0.8f)],
            Keyword = [Hit(c, 1f), Hit(d, 0.5f)],
        };
        var embed = new FixedEmbedding([0.1f, 0.2f]);

        var results = await Search(Service(store, embed), SearchModes.Hybrid);

        // a と c は共に 1/61、b と d は共に 1/62 —— 同点は「ベクトル側が先に現れる」の従来規則。
        results.Select(r => r.ChunkId).Should().Equal(a, c, b, d);
        results.Select(r => r.Score).Should().Equal(
            (float)(1.0 / 61), (float)(1.0 / 61), (float)(1.0 / 62), (float)(1.0 / 62));
        embed.Calls.Should().Be(1);
        store.VectorCalls.Should().Be(1);
        store.KeywordCalls.Should().Be(1);
    }

    // T-F-02: keyword / semantic は**RRF を通さず**、ストアの並びと生スコアをそのまま返す（従来どおり）。
    [Theory]
    [InlineData(SearchModes.Keyword)]
    [InlineData(SearchModes.Semantic)]
    public async Task 追加が空なら単一モードはストアの並びと生スコアをそのまま返す(string mode)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var list = new List<SearchResultDto> { Hit(a, 0.7f), Hit(b, 0.3f) };
        var store = new ScriptedStore { Vector = list, Keyword = list };
        var embed = new FixedEmbedding([0.1f]);

        var results = await Search(Service(store, embed), mode);

        results.Select(r => (r.ChunkId, r.Score)).Should().Equal((a, 0.7f), (b, 0.3f));
        embed.Calls.Should().Be(mode == SearchModes.Keyword ? 0 : 1);
        (store.VectorCalls + store.KeywordCalls).Should().Be(1);
    }

    // T-F-03: 引数を渡さない構築（既存の全試験と同じ形）と `FusedCollections.None` は同じ結果になる。
    [Theory]
    [InlineData(SearchModes.Hybrid)]
    [InlineData(SearchModes.Keyword)]
    [InlineData(SearchModes.Semantic)]
    public async Task 追加を渡さない構築とNoneは同じ結果になる(string mode)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ScriptedStore NewStore() => new() { Vector = [Hit(a, 0.4f)], Keyword = [Hit(b, 0.6f), Hit(a, 0.1f)] };

        var legacy = await new HybridSearchService(
                NewStore(), new FixedEmbedding([1f]), NullLogger<HybridSearchService>.Instance)
            .SearchAsync(new SearchRequest("問い", 10, null, Granted, mode), TestSearchUser.Any,
                TestContext.Current.CancellationToken);
        var none = await Search(Service(NewStore(), new FixedEmbedding([1f])), mode);

        none.Select(r => (r.ChunkId, r.Score)).Should().Equal(legacy.Select(r => (r.ChunkId, r.Score)));
    }

    // ---- (2) 順位で束ねる（決定 1） -----------------------------------------------------

    // T-F-04: 🔴 **生スコアを比べない。** ティア A 側の生スコア（0.99 など）は主の生スコア（0.1）より
    // 桁違いに大きいが、モデルが違えば同じ意味を持たない。**順位だけで並ぶ**ことを固定する。
    // 変異 M-2（融合を生スコアの降順にする）はここで赤になる。
    [Fact]
    public async Task 追加コレクションのヒットは生スコアではなく順位で並ぶ()
    {
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid();
        var a1 = Guid.NewGuid(); var a2 = Guid.NewGuid(); var a3 = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(p1, 0.10f), Hit(p2, 0.09f)], Keyword = [Hit(p1, 1f)] };
        var tierA = new ScriptedStore { Vector = [Hit(a1, 0.99f), Hit(a2, 0.98f), Hit(a3, 0.97f)], Keyword = [] };

        var results = await Search(
            Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, new FixedEmbedding([2f]))),
            SearchModes.Hybrid);

        // p1 = 1/61 + 1/61（両系統）/ a1 = 1/61 / p2 = 1/62 / a2 = 1/62 / a3 = 1/63
        results.Select(r => r.ChunkId).Should().Equal(p1, a1, p2, a2, a3);
        // 返す Score は RRF の合算値であり、入力の生スコアではない。
        results[1].Score.Should().Be((float)(1.0 / 61));
        results.Should().NotContain(r => r.Score > 0.5f, "生スコアが結果へ漏れていない");
    }

    // T-F-05: 同点は**主が先**（入力の並び: 主ベクトル → 主全文 → 追加ベクトル → 追加全文）。
    [Fact]
    public async Task 同点は主コレクションが先に並ぶ()
    {
        var p = Guid.NewGuid(); var a = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(p, 0.1f)], Keyword = [] };
        var tierA = new ScriptedStore { Vector = [Hit(a, 0.9f)], Keyword = [] };

        var results = await Search(
            Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, new FixedEmbedding([2f]))),
            SearchModes.Hybrid);

        results.Select(r => r.ChunkId).Should().Equal(p, a);
        results[0].Score.Should().Be(results[1].Score);
    }

    // T-F-06: RRF の同点規則そのもの（純関数）。**二次キー（初出順）を明示した**ことで、辞書の列挙順に依存しない。
    [Fact]
    public void Rrfの同点は入力の並びで先に現れた方が先()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid(); var z = Guid.NewGuid();

        var fused = HybridSearchService.ReciprocalRankFusion(
            [Hit(x, 0f)], [Hit(y, 0f)], [Hit(z, 0f)]);

        fused.Select(r => r.ChunkId).Should().Equal(x, y, z);
    }

    // T-F-07: 🔴 **全文も束ねる**（ADR-0092 実測 6）。keyword モードでティア A の文書が見つかる。
    [Fact]
    public async Task keywordモードでも追加コレクションの文書が見つかる()
    {
        var p = Guid.NewGuid(); var a = Guid.NewGuid();
        var primary = new ScriptedStore { Keyword = [Hit(p, 1f)] };
        var tierA = new ScriptedStore { Keyword = [Hit(a, 1f)] };
        var tierAEmbed = new FixedEmbedding([2f]);

        var results = await Search(
            Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, tierAEmbed)),
            SearchModes.Keyword);

        results.Select(r => r.ChunkId).Should().Equal(p, a);
        tierA.KeywordCalls.Should().Be(1);
        tierA.VectorCalls.Should().Be(0, "keyword モードはベクトル系統を引かない");
        tierAEmbed.Calls.Should().Be(0, "keyword モードは埋め込みを呼ばない");
    }

    // T-F-08: semantic モード。主が埋め込めなくても、追加コレクションが埋め込めればその並びを返す。
    [Fact]
    public async Task semanticモードは埋め込めたコレクションだけで束ねる()
    {
        var a = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)] };
        var tierA = new ScriptedStore { Vector = [Hit(a, 0.5f)] };

        var results = await Search(
            Service(primary, new FixedEmbedding([]), new FusedCollection(Ruri, tierA, new FixedEmbedding([2f]))),
            SearchModes.Semantic);

        results.Select(r => r.ChunkId).Should().Equal(a);
        primary.VectorCalls.Should().Be(0, "空ベクトルをベクトルDB へ渡さない（#995）");
    }

    // T-F-09: semantic モードで**どのコレクションも**埋め込めなければ 0 件（従来の semantic と同じ意味）。
    [Fact]
    public async Task semanticモードで全コレクションが埋め込めなければ0件()
    {
        var primary = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)] };
        var tierA = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)] };

        var results = await Search(
            Service(primary, new FixedEmbedding([]), new FusedCollection(Ruri, tierA, new FixedEmbedding([]))),
            SearchModes.Semantic);

        results.Should().BeEmpty();
        (primary.VectorCalls + tierA.VectorCalls).Should().Be(0);
    }

    // T-F-10: hybrid で追加コレクションだけ埋め込めない（ティア A の推論基盤の不調）とき、
    // その**ベクトル系統だけを落とし、全文は引く**（主の #995 と同じ縮退の向き）。
    [Fact]
    public async Task hybridで追加コレクションが埋め込めなければ全文だけ引く()
    {
        var a = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)], Keyword = [] };
        var tierA = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)], Keyword = [Hit(a, 1f)] };

        var results = await Search(
            Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, new FixedEmbedding([]))),
            SearchModes.Hybrid);

        tierA.VectorCalls.Should().Be(0);
        tierA.KeywordCalls.Should().Be(1);
        results.Should().Contain(r => r.ChunkId == a);
    }

    // T-F-11: 追加コレクションの埋め込みは**そのコレクションの客体**で得て、そのベクトルで引く
    // （主のベクトルをティア A のコレクションへ投げない。ADR-0092 実測 4）。
    [Fact]
    public async Task 追加コレクションはそのコレクションの埋め込みで引く()
    {
        var primary = new ScriptedStore();
        var tierA = new ScriptedStore();
        var tierAEmbed = new FixedEmbedding([7f, 7f, 7f]);

        await Search(
            Service(primary, new FixedEmbedding([1f, 1f]), new FusedCollection(Ruri, tierA, tierAEmbed)),
            SearchModes.Hybrid);

        tierAEmbed.Calls.Should().Be(1);
        tierA.LastVector.Should().Equal(7f, 7f, 7f);
        primary.LastVector.Should().Equal(1f, 1f);
    }

    // T-F-18: 🔴 **束ねても候補幅は変えない**（[[IADR-0467]] 決定 2「候補幅」）。hybrid は各系統 `max(TopK*4, TopK)`、
    // keyword / semantic は単一モードの幅（relevance なら `TopK`、updated なら hybrid と同じ幅）を、主にも追加にも
    // **同じ値で**渡す。束ねた semantic の経路へ別の幅（例: 常に `max(TopK*4, TopK)`）を渡す変異はここで赤になる。
    [Theory]
    [InlineData(SearchModes.Semantic, SearchSorts.Relevance, 10)]
    [InlineData(SearchModes.Semantic, SearchSorts.Updated, 40)]
    [InlineData(SearchModes.Keyword, SearchSorts.Relevance, 10)]
    [InlineData(SearchModes.Keyword, SearchSorts.Updated, 40)]
    [InlineData(SearchModes.Hybrid, SearchSorts.Relevance, 40)]
    [InlineData(SearchModes.Hybrid, SearchSorts.Updated, 40)]
    public async Task 束ねても各系統の候補幅は単一コレクションのときと同じ(string mode, string sort, int expected)
    {
        var primary = new ScriptedStore();
        var tierA = new ScriptedStore();

        await Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, new FixedEmbedding([2f])))
            .SearchAsync(new SearchRequest("問い", 10, null, Granted, mode, sort), TestSearchUser.Any,
                TestContext.Current.CancellationToken);

        // 陽性対照: 追加コレクションが無いときの主の幅（従来の経路）と同じであること。
        var alone = new ScriptedStore();
        await Service(alone, new FixedEmbedding([1f]))
            .SearchAsync(new SearchRequest("問い", 10, null, Granted, mode, sort), TestSearchUser.Any,
                TestContext.Current.CancellationToken);

        alone.Limits.Should().NotBeEmpty().And.AllSatisfy(k => k.Should().Be(expected));
        primary.Limits.Should().HaveCount(alone.Limits.Count).And.AllSatisfy(k => k.Should().Be(expected));
        tierA.Limits.Should().HaveCount(alone.Limits.Count).And.AllSatisfy(k => k.Should().Be(expected));
    }

    // ---- (3) ABAC は全コレクションの全系統に掛かる（決定 3） ------------------------------

    private static readonly AccessScope PublicOnly =
        new([new AttributeFilter("confidentiality", ["public"])], true);

    // T-F-12: 🔴 **高機密を許さないスコープでも、追加コレクションへの問い合わせは省かれず、
    // 主と同じフィルタが全系統に渡る。** 変異 M-1（追加側へ null / 空のフィルタを渡す）はここで赤になる。
    [Theory]
    [InlineData(SearchModes.Hybrid)]
    [InlineData(SearchModes.Keyword)]
    [InlineData(SearchModes.Semantic)]
    public async Task 追加コレクションの全系統に主と同じABACフィルタが渡る(string mode)
    {
        var primary = new ScriptedStore();
        var tierA = new ScriptedStore();

        await Search(
            Service(primary, new FixedEmbedding([1f]), new FusedCollection(Ruri, tierA, new FixedEmbedding([2f]))),
            mode, PublicOnly);

        // 問い合わせは省かれていない（省略を統制の担い手にしない）。
        tierA.Filters.Should().NotBeEmpty();
        tierA.Filters.Count.Should().Be(primary.Filters.Count);
        foreach (var f in tierA.Filters)
        {
            f.Should().NotBeNull();
            f!.IsUnconstrained.Should().BeFalse("全件許可のフィルタで追加コレクションを引いてはならない");
            f.Conjunction.Should().ContainSingle(c => c.Key == "confidentiality")
                .Which.AllowedValues.Should().Equal("public");
        }

        tierA.Filters.Should().AllSatisfy(f => f.Should().BeSameAs(primary.Filters[0]));
    }

    // T-F-13: 実際に絞れていることを**フィルタを解釈するストア**（`InMemoryVectorStore`）で確かめる。
    // 陽性対照（高機密を許すスコープでは出る）と対にする —— 片方だけでは「そもそも引いていない」でも緑になる。
    [Theory]
    [InlineData(SearchModes.Hybrid)]
    [InlineData(SearchModes.Keyword)]
    [InlineData(SearchModes.Semantic)]
    public async Task 権限外の高機密文書はフィルタで落ち_権限内なら見つかる(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var confidentialDoc = Guid.NewGuid();
        var tierA = new InMemoryVectorStore();
        await tierA.UpsertAsync(new ChunkPayload(
            Guid.NewGuid(), confidentialDoc, "人事規程", "人事 規程 本文", [0.1f, 0.2f], null,
            new Dictionary<string, string> { ["confidentiality"] = "confidential" }, []), ct);

        HybridSearchService NewSvc() => Service(new InMemoryVectorStore(), new FixedEmbedding([1f]),
            new FusedCollection(Ruri, tierA, new FixedEmbedding([2f])));

        var denied = await NewSvc().SearchAsync(
            new SearchRequest("人事", 10, null, PublicOnly, mode), TestSearchUser.Any, ct);
        var allowed = await NewSvc().SearchAsync(
            new SearchRequest("人事", 10, null,
                new AccessScope([new AttributeFilter("confidentiality", ["public", "confidential"])], true), mode),
            TestSearchUser.Any, ct);

        denied.Should().NotContain(r => r.DocumentId == confidentialDoc);
        allowed.Should().Contain(r => r.DocumentId == confidentialDoc);
    }

    // T-F-14: deny-by-default は**全コレクションの手前**で効く（追加コレクションも埋め込みも呼ばない）。
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task denyByDefaultなら追加コレクションも呼ばない(bool? grants)
    {
        var tierA = new ScriptedStore();
        var tierAEmbed = new FixedEmbedding([2f]);
        var scope = grants is null ? null : new AccessScope([], grants.Value);

        var results = await Service(new ScriptedStore(), new FixedEmbedding([1f]),
                new FusedCollection(Ruri, tierA, tierAEmbed))
            .SearchAsync(new SearchRequest("問い", 10, null, scope), TestSearchUser.Any,
                TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
        (tierA.VectorCalls + tierA.KeywordCalls).Should().Be(0);
        tierAEmbed.Calls.Should().Be(0);
    }

    // ---- 属性値・削除（母集合 #5・#6） ---------------------------------------------------

    // T-F-15: 権限内属性値は全コレクションの和集合（件数は持たない）。各コレクションへ同じ制約を渡す。
    [Fact]
    public async Task 権限内属性値は全コレクションの和集合を返す()
    {
        var primary = new ScriptedStore { Values = ["internal", "public"] };
        var tierA = new ScriptedStore { Values = ["confidential", "public"] };
        var scope = new AccessScope([new AttributeFilter("department", ["hr"])], true);

        var values = await AttributeValuesEndpoint.ListAsync(
            primary, new FusedCollections([new FusedCollection(Ruri, tierA, new FixedEmbedding([]))]),
            "confidentiality", scope, TestContext.Current.CancellationToken);

        values.Should().Equal("confidential", "internal", "public");
        tierA.Filters.Should().ContainSingle().Which!.Conjunction
            .Should().ContainSingle(c => c.Key == "department");
    }

    // T-F-16: 追加が空なら主の答えをそのまま返す（従来と同一）。
    [Fact]
    public async Task 追加が空なら属性値は主の答えのまま()
    {
        var primary = new ScriptedStore { Values = ["b", "a"] };

        var values = await AttributeValuesEndpoint.ListAsync(
            primary, FusedCollections.None, "confidentiality", new AccessScope([], true),
            TestContext.Current.CancellationToken);

        values.Should().Equal("b", "a");
    }

    // T-F-17: 削除は検索が読む全コレクションから行う（ADR-0057 決定 1）。
    [Fact]
    public async Task 削除は追加コレクションからも行う()
    {
        var primary = new ScriptedStore();
        var tierA = new ScriptedStore();
        var doc = Guid.NewGuid();

        await new DocumentDeletedConsumer(
                primary, new FusedCollections([new FusedCollection(Ruri, tierA, new FixedEmbedding([]))]),
                NullLogger<DocumentDeletedConsumer>.Instance)
            .Handle(new DocumentDeleted(doc, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        primary.Deleted.Should().Equal(doc);
        tierA.Deleted.Should().Equal(doc);
    }
}

// 呼び出しと、渡されたフィルタを記録するストア（融合の順位・ABAC の伝播を観測する）。
internal sealed class ScriptedStore : IVectorStore
{
    public List<SearchResultDto> Vector { get; init; } = [];
    public List<SearchResultDto> Keyword { get; init; } = [];
    public List<string> Values { get; init; } = [];
    public int VectorCalls { get; private set; }
    public int KeywordCalls { get; private set; }
    public float[]? LastVector { get; private set; }
    public List<ScopeFilter?> Filters { get; } = [];
    public List<Guid> Deleted { get; } = [];
    // ベクトル・全文の各問い合わせへ渡された候補幅（topK）を呼び出し順に記録する（決定 2 の候補幅を観測する）。
    public List<int> Limits { get; } = [];

    public Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK, ScopeFilter? filters, CancellationToken ct = default)
    {
        VectorCalls++;
        LastVector = queryVector;
        Limits.Add(topK);
        Filters.Add(filters);
        return Task.FromResult(Vector.ToList());
    }

    public Task<List<SearchResultDto>> KeywordSearchAsync(
        string query, int topK, ScopeFilter? filters, CancellationToken ct = default)
    {
        KeywordCalls++;
        Limits.Add(topK);
        Filters.Add(filters);
        return Task.FromResult(Keyword.ToList());
    }

    public Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
        float[] queryVector, int topK, IReadOnlyCollection<Guid> documentIds,
        ScopeFilter? filters, CancellationToken ct = default)
        => Task.FromResult(new List<SearchResultDto>());

    public Task<List<string>> ListAttributeValuesAsync(
        string payloadKey, ScopeFilter? filters, CancellationToken ct = default)
    {
        Filters.Add(filters);
        return Task.FromResult(Values.ToList());
    }

    public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        Deleted.Add(documentId);
        return Task.CompletedTask;
    }
}

// 決まったベクトルを返し、呼び出し回数を数える埋め込み（空配列を与えれば「埋め込めない」縮退を再現する）。
internal sealed class FixedEmbedding(float[] vector) : IEmbeddingService
{
    public int Calls { get; private set; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(vector);
    }
}
