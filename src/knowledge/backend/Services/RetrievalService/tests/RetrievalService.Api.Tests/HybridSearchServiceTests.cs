using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Foundation.Services;

namespace RetrievalService.Api.Tests;

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
}
