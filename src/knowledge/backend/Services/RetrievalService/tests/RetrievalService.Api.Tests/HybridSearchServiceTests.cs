using FluentAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Foundation.Ports;
using RetrievalService.Api.Foundation.Services;

namespace RetrievalService.Api.Tests;

// FR-03, SC-02, #531: 検索モード（3 値）の分岐を検証するための記録用スタブ。
// どちらの系統が呼ばれたか（＝モードの効き）を観測する。
internal sealed class RecordingVectorStore : IVectorStore
{
    public int VectorCalls { get; private set; }
    public int KeywordCalls { get; private set; }
    public int LastVectorTopK { get; private set; }
    public int LastKeywordTopK { get; private set; }
    public List<SearchResultDto> VectorResults { get; init; } = [];
    public List<SearchResultDto> KeywordResults { get; init; } = [];

    public Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK, IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
    {
        VectorCalls++;
        LastVectorTopK = topK;
        return Task.FromResult(VectorResults);
    }

    public Task<List<SearchResultDto>> KeywordSearchAsync(
        string query, int topK, IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
    {
        KeywordCalls++;
        LastKeywordTopK = topK;
        return Task.FromResult(KeywordResults);
    }

    public Task<List<string>> ListAttributeValuesAsync(
        string payloadKey, IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
        => Task.FromResult<List<string>>([]);

    public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class CountingEmbeddingService : IEmbeddingService
{
    public int Calls { get; private set; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new[] { 0.1f, 0.2f });
    }
}

// FR-03, UC-01: ハイブリッド検索の核 — Reciprocal Rank Fusion ロジックの単体テスト
public class HybridSearchServiceTests
{
    private static SearchResultDto Hit(Guid id, float score = 0f, DateTimeOffset? updatedAt = null) =>
        new(id, Guid.NewGuid(), "title", "text", score, "uri", new(), [], updatedAt);

    // FR-03: 両系統（ベクトル/全文）に現れる文書は、片方のみの文書より上位になる
    [Fact]
    public void Rrf_RanksDocumentsAppearingInBothLists_Higher()
    {
        var both = Guid.NewGuid();
        var vectorOnly = Guid.NewGuid();
        var keywordOnly = Guid.NewGuid();

        var vector = new List<SearchResultDto> { Hit(vectorOnly), Hit(both) };
        var keyword = new List<SearchResultDto> { Hit(keywordOnly), Hit(both) };

        var fused = HybridSearchService.ReciprocalRankFusion(vector, keyword);

        fused[0].ChunkId.Should().Be(both, "両リストに出現する文書が最上位になる");
        fused.Select(r => r.ChunkId).Should().Contain([vectorOnly, keywordOnly]);
    }

    // FR-03, SC-02, #536: **融合で更新日時が落ちない。** RRF はスコアだけを差し替える（`with { Score = … }`）
    // が、射影を書き直したときに黙って欠ける型の変更である。契約に載せた項目は融合の後も残ることを固定する。
    [Fact]
    public void Rrf_PreservesUpdatedAt()
    {
        var id = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var fused = HybridSearchService.ReciprocalRankFusion(
            [Hit(id, updatedAt: updatedAt)], [Hit(id, updatedAt: updatedAt)]);

        fused.Should().ContainSingle();
        fused[0].UpdatedAt.Should().Be(updatedAt);
    }

    // 片方だけが日時を持つ場合も、残った側の値が消えない（未再索引と再索引済みが混在する期間の振る舞い）。
    [Fact]
    public void Rrf_KeepsUpdatedAt_WhenOnlyOneSideWasReindexed()
    {
        var reindexed = Guid.NewGuid();
        var notYet = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var fused = HybridSearchService.ReciprocalRankFusion(
            [Hit(reindexed, updatedAt: updatedAt), Hit(notYet)], []);

        fused.Single(r => r.ChunkId == reindexed).UpdatedAt.Should().Be(updatedAt);
        fused.Single(r => r.ChunkId == notYet).UpdatedAt.Should().BeNull();
    }

    // FR-03: 片方のリストにしか無い文書も結果に含まれる（取りこぼさない）
    [Fact]
    public void Rrf_IncludesDocumentsFromEitherList()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var fused = HybridSearchService.ReciprocalRankFusion(
            new List<SearchResultDto> { Hit(a) },
            new List<SearchResultDto> { Hit(b) });

        fused.Should().HaveCount(2);
        fused.Select(r => r.ChunkId).Should().BeEquivalentTo([a, b]);
    }

    // FR-03: 融合スコアは順位ベースで再計算され、降順に並ぶ
    [Fact]
    public void Rrf_AssignsDescendingFusedScores()
    {
        var top = Guid.NewGuid();
        var fused = HybridSearchService.ReciprocalRankFusion(
            new List<SearchResultDto> { Hit(top), Hit(Guid.NewGuid()) },
            new List<SearchResultDto> { Hit(top) });

        fused[0].ChunkId.Should().Be(top);
        fused.Should().BeInDescendingOrder(r => r.Score);
        // 1/(60+1) + 1/(60+1) = 2/61
        fused[0].Score.Should().BeApproximately(2f / 61f, 1e-5f);
    }

    [Fact]
    public void Rrf_WithNoRankings_ReturnsEmpty()
    {
        var fused = HybridSearchService.ReciprocalRankFusion(
            new List<SearchResultDto>(), new List<SearchResultDto>());
        fused.Should().BeEmpty();
    }

    // --- FR-03, SC-02, #531: 検索モード（3 値）------------------------------------

    private static readonly AccessScope Granted = new([], true);

    private static (HybridSearchService svc, RecordingVectorStore store, CountingEmbeddingService embed) NewService(
        List<SearchResultDto>? vectorResults = null, List<SearchResultDto>? keywordResults = null)
    {
        var store = new RecordingVectorStore
        {
            VectorResults = vectorResults ?? [Hit(Guid.NewGuid())],
            KeywordResults = keywordResults ?? [Hit(Guid.NewGuid())],
        };
        var embed = new CountingEmbeddingService();
        return (new HybridSearchService(store, embed), store, embed);
    }

    // 既定（Mode 未指定）は従来どおりハイブリッド＝両系統を呼ぶ（後方互換）。
    [Fact]
    public async Task Mode_DefaultsToHybrid_AndQueriesBothSystems()
    {
        var (svc, store, embed) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 10, null, Granted));

        store.VectorCalls.Should().Be(1);
        store.KeywordCalls.Should().Be(1);
        embed.Calls.Should().Be(1);
    }

    // keyword: 全文検索のみ。**埋め込みを生成しない**（無駄な LLM 呼び出しをしない）。
    [Fact]
    public async Task Mode_Keyword_QueriesOnlyKeywordSystem()
    {
        var (svc, store, embed) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 10, null, Granted, SearchModes.Keyword));

        store.KeywordCalls.Should().Be(1);
        store.VectorCalls.Should().Be(0);
        embed.Calls.Should().Be(0, "キーワード検索で埋め込みを生成する必要は無い");
    }

    // semantic: ベクトル検索のみ。
    [Fact]
    public async Task Mode_Semantic_QueriesOnlyVectorSystem()
    {
        var (svc, store, embed) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 10, null, Granted, SearchModes.Semantic));

        store.VectorCalls.Should().Be(1);
        store.KeywordCalls.Should().Be(0);
        embed.Calls.Should().Be(1);
    }

    // 単系統では融合しないため候補を広げる意味が無い。topK をそのまま使う
    // （hybrid は融合精度のため topK*4 を取りに行く）。
    [Fact]
    public async Task Mode_SingleSystem_DoesNotOverFetchCandidates()
    {
        var (svc, store, _) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 5, null, Granted, SearchModes.Keyword));
        store.LastKeywordTopK.Should().Be(5);

        await svc.SearchAsync(new SearchRequest("q", 5, null, Granted, SearchModes.Hybrid));
        store.LastKeywordTopK.Should().Be(20, "hybrid は融合のため候補を広く取る");
    }

    // 未知の値・空文字は既定（hybrid）へ縮退する＝旧クライアント／誤入力で検索が壊れない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-mode")]
    public async Task Mode_UnknownValue_FallsBackToHybrid(string? mode)
    {
        var (svc, store, _) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 10, null, Granted, mode));

        store.VectorCalls.Should().Be(1);
        store.KeywordCalls.Should().Be(1);
    }

    // 大小文字は無視する（"Keyword" でも keyword として扱う）。
    [Fact]
    public async Task Mode_IsCaseInsensitive()
    {
        var (svc, store, _) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 10, null, Granted, "KEYWORD"));

        store.KeywordCalls.Should().Be(1);
        store.VectorCalls.Should().Be(0);
    }

    // FR-05: モードを指定しても deny-by-default は変わらない（スコープ無しは空）。
    [Fact]
    public async Task Mode_DoesNotBypassDenyByDefault()
    {
        var (svc, store, _) = NewService();

        var results = await svc.SearchAsync(new SearchRequest("q", 10, null, null, SearchModes.Keyword));

        results.Should().BeEmpty();
        store.KeywordCalls.Should().Be(0, "スコープ未解決なら系統を一切呼ばない");
    }

    // 値集合そのものの回帰。**2 値にしてはならない**（利用者裁定 Q4・planning#197）。
    [Fact]
    public void SearchModes_HasExactlyThreeValues_WithHybridDefault()
    {
        SearchModes.All.Should().BeEquivalentTo([SearchModes.Hybrid, SearchModes.Keyword, SearchModes.Semantic]);
        SearchModes.Normalize(null).Should().Be(SearchModes.Hybrid);
        SearchModes.IsValid("semantic").Should().BeTrue();
        SearchModes.IsValid("fuzzy").Should().BeFalse();
    }

    // --- FR-03, SC-02, #532: 並び順（2 値）--------------------------------------

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);

    // 既定（SortBy 未指定）は関連度順のまま＝**現行の振る舞いを一切変えない**（後方互換）。
    // 取得順（＝関連度順）がそのまま返ることを、あえて日時の逆順に積んで確かめる。
    [Fact]
    public async Task Sort_DefaultsToRelevance_AndKeepsRetrievalOrder()
    {
        var old = Hit(Guid.NewGuid(), updatedAt: At(1, 1));
        var recent = Hit(Guid.NewGuid(), updatedAt: At(12, 31));
        var (svc, _, _) = NewService(keywordResults: [old, recent]);

        var results = await svc.SearchAsync(new SearchRequest("q", 10, null, Granted, SearchModes.Keyword));

        results.Select(r => r.ChunkId).Should().Equal([old.ChunkId, recent.ChunkId],
            "既定では取得順（関連度順）を並べ替えない");
    }

    // updated: 更新日時の降順になる（利用者裁定 Q5「更新日時の新しい順」）。
    [Fact]
    public async Task Sort_Updated_OrdersByUpdatedAtDescending()
    {
        var mid = Hit(Guid.NewGuid(), updatedAt: At(6, 1));
        var oldest = Hit(Guid.NewGuid(), updatedAt: At(1, 1));
        var newest = Hit(Guid.NewGuid(), updatedAt: At(12, 31));
        var (svc, _, _) = NewService(keywordResults: [mid, oldest, newest]);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, SearchModes.Keyword, SearchSorts.Updated));

        results.Select(r => r.ChunkId).Should().Equal(newest.ChunkId, mid.ChunkId, oldest.ChunkId);
    }

    // **日時を持たないチャンク（未再索引・IADR-0149 決定 3）は末尾**（IADR-0150 決定 4）。
    // 「新しい順」の先頭が日時不明で埋まらないようにする。再索引で解消する一時状態を先頭に出さない。
    [Fact]
    public async Task Sort_Updated_PutsChunksWithoutDateLast()
    {
        var unknown = Hit(Guid.NewGuid());
        var oldest = Hit(Guid.NewGuid(), updatedAt: At(1, 1));
        var newest = Hit(Guid.NewGuid(), updatedAt: At(12, 31));
        var (svc, _, _) = NewService(keywordResults: [unknown, oldest, newest]);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, SearchModes.Keyword, SearchSorts.Updated));

        results.Select(r => r.ChunkId).Should().Equal([newest.ChunkId, oldest.ChunkId, unknown.ChunkId],
            "日時なしは末尾。DateTimeOffset.MinValue 扱いにすると『とても古い文書』と区別できなくなる");
    }

    // 同着（同じ日時・日時なし同士）は**元の順序＝関連度の順**を保つ（安定ソート）。
    [Fact]
    public async Task Sort_Updated_IsStableForTies()
    {
        var same = At(6, 1);
        var first = Hit(Guid.NewGuid(), updatedAt: same);
        var second = Hit(Guid.NewGuid(), updatedAt: same);
        var noDateFirst = Hit(Guid.NewGuid());
        var noDateSecond = Hit(Guid.NewGuid());
        var (svc, _, _) = NewService(keywordResults: [first, second, noDateFirst, noDateSecond]);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, SearchModes.Keyword, SearchSorts.Updated));

        results.Select(r => r.ChunkId).Should()
            .Equal(first.ChunkId, second.ChunkId, noDateFirst.ChunkId, noDateSecond.ChunkId);
    }

    // 未知の値・空文字は既定（relevance）へ縮退する＝旧クライアント／誤入力で検索が壊れない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-sort")]
    public async Task Sort_UnknownValue_FallsBackToRelevance(string? sort)
    {
        var old = Hit(Guid.NewGuid(), updatedAt: At(1, 1));
        var recent = Hit(Guid.NewGuid(), updatedAt: At(12, 31));
        var (svc, _, _) = NewService(keywordResults: [old, recent]);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, SearchModes.Keyword, sort));

        results.Select(r => r.ChunkId).Should().Equal(old.ChunkId, recent.ChunkId);
    }

    // 大文字小文字は問わない（SearchModes と同じ作法）。
    [Fact]
    public async Task Sort_IsCaseInsensitive()
    {
        var old = Hit(Guid.NewGuid(), updatedAt: At(1, 1));
        var recent = Hit(Guid.NewGuid(), updatedAt: At(12, 31));
        var (svc, _, _) = NewService(keywordResults: [old, recent]);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, SearchModes.Keyword, "UPDATED"));

        results.Select(r => r.ChunkId).Should().Equal(recent.ChunkId, old.ChunkId);
    }

    // **日時順のときは単系統でも候補を広げる**（IADR-0150 決定 3）。並べ替える以上、候補が広いほど
    // 「もっと新しい関連文書」を拾える。融合と同じ幅（topK*4）に揃える。
    [Fact]
    public async Task Sort_Updated_WidensCandidatesEvenForSingleMode()
    {
        var (svc, store, _) = NewService();

        await svc.SearchAsync(new SearchRequest("q", 5, null, Granted, SearchModes.Keyword));
        store.LastKeywordTopK.Should().Be(5, "関連度順なら単系統は広げない（従来どおり）");

        await svc.SearchAsync(
            new SearchRequest("q", 5, null, Granted, SearchModes.Keyword, SearchSorts.Updated));
        store.LastKeywordTopK.Should().Be(20, "日時順は並べ替えるので候補を広く取る");
    }

    // 候補を広げても**返すのは topK 件**である（広げた分をそのまま返さない）。
    [Fact]
    public async Task Sort_Updated_StillReturnsOnlyTopK()
    {
        var hits = Enumerable.Range(1, 12)
            .Select(i => Hit(Guid.NewGuid(), updatedAt: At(1, i))).ToList();
        var (svc, _, _) = NewService(keywordResults: hits);

        var results = await svc.SearchAsync(
            new SearchRequest("q", 3, null, Granted, SearchModes.Keyword, SearchSorts.Updated));

        results.Should().HaveCount(3);
        results[0].UpdatedAt.Should().Be(At(1, 12), "最も新しい 3 件が返る");
    }
}
