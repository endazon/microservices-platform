using KnowledgePlatform.Shared.Contracts.Dtos;
using RetrievalService.Api.Abstractions;

namespace RetrievalService.Api.Search;

// FR-03, UC-01: ベクトル検索と全文検索を Reciprocal Rank Fusion で統合するハイブリッド検索
public class HybridSearchService(IVectorStore store, IEmbeddingService embed)
    : IHybridSearchService
{
    // RRF の平滑化定数（順位ベース統合。上位の影響を緩める一般的な既定値）
    internal const int RrfK = 60;

    public async Task<List<SearchResultDto>> SearchAsync(
        SearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        // 融合精度のため topK より広めの候補を各系統から取得する
        var candidateK = Math.Max(request.TopK * 4, request.TopK);

        // FR-03: 意味検索（ベクトル）と全文検索（キーワード）を並行実行し p95 を抑える
        var vector = await embed.EmbedAsync(request.Query, ct);
        var vectorTask = store.SearchAsync(vector, candidateK, request.AttributeFilters, ct);
        var keywordTask = store.KeywordSearchAsync(request.Query, candidateK, request.AttributeFilters, ct);
        await Task.WhenAll(vectorTask, keywordTask);

        // FR-03: 順位ベースで両系統を統合（スコアのスケール差を正規化なしで吸収）
        var fused = ReciprocalRankFusion(vectorTask.Result, keywordTask.Result);
        return fused.Take(request.TopK).ToList();
    }

    // FR-03: Reciprocal Rank Fusion。両リストに現れる文書ほど上位になる。
    internal static List<SearchResultDto> ReciprocalRankFusion(
        params IReadOnlyList<SearchResultDto>[] rankings)
    {
        var scores = new Dictionary<Guid, double>();
        var byId = new Dictionary<Guid, SearchResultDto>();

        foreach (var ranking in rankings)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var hit = ranking[rank];
                scores[hit.ChunkId] = scores.GetValueOrDefault(hit.ChunkId) + 1.0 / (RrfK + rank + 1);
                // 最初に出会ったペイロードを採用（出典情報は同一チャンクで一致）
                byId.TryAdd(hit.ChunkId, hit);
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => byId[kv.Key] with { Score = (float)kv.Value })
            .ToList();
    }
}
