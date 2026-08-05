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
    private static SearchResultDto Hit(Guid id, float score = 0f) =>
        new(id, Guid.NewGuid(), "title", "text", score, "uri", new(), []);

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

    private static (HybridSearchService svc, RecordingVectorStore store, CountingEmbeddingService embed) NewService()
    {
        var store = new RecordingVectorStore
        {
            VectorResults = [Hit(Guid.NewGuid())],
            KeywordResults = [Hit(Guid.NewGuid())],
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
}
