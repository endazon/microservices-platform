using KnowledgePlatform.Shared.Contracts.Dtos;
using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Foundation.Services;

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

        // FR-05: deny-by-default（fail-closed）。IADR-0012。
        //   Scope 未指定（null）＝呼び出し側が ABAC スコープを解決していない、
        //   GrantsAccess=false＝許可ポリシーが無い（閲覧可能文書なし）。
        //   いずれも「何も返さない」に倒す。GrantsAccess=true の明示的許可がある時だけ検索する。
        //   ここを null 許容にすると Scope 無しの呼び出しがフィルタ無しで全文書を返し、
        //   ネットワーク到達可能な相手が ABAC を全面バイパスできてしまう（呼び出し側 Scope の無検証信任）。
        if (request.Scope is not { GrantsAccess: true })
            return [];

        // FR-05: 単値フィルタ（後方互換）と ABAC 多値スコープを 1 本の allow-list に正規化する。
        var filters = BuildFilters(request);

        // 融合精度のため topK より広めの候補を各系統から取得する
        var candidateK = Math.Max(request.TopK * 4, request.TopK);

        // FR-03: 意味検索（ベクトル）と全文検索（キーワード）を並行実行し p95 を抑える
        var vector = await embed.EmbedAsync(request.Query, ct);
        var vectorTask = store.SearchAsync(vector, candidateK, filters, ct);
        var keywordTask = store.KeywordSearchAsync(request.Query, candidateK, filters, ct);
        await Task.WhenAll(vectorTask, keywordTask);

        // FR-03: 順位ベースで両系統を統合（スコアのスケール差を正規化なしで吸収）
        var fused = ReciprocalRankFusion(vectorTask.Result, keywordTask.Result);
        return fused.Take(request.TopK).ToList();
    }

    // FR-05: 単値 AttributeFilters（FR-03 後方互換）と ABAC 多値 Scope を 1 本の allow-list へ統合。
    // 同一キーが両方に現れた場合は値集合を結合（OR）する。
    private static List<AttributeFilter>? BuildFilters(SearchRequest request)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (request.AttributeFilters is { Count: > 0 })
            foreach (var (key, value) in request.AttributeFilters)
                Add(key, [value]);

        if (request.Scope is { Filters.Count: > 0 } scope)
            foreach (var f in scope.Filters)
                Add(f.Key, f.AllowedValues);

        if (byKey.Count == 0)
            return null;

        return byKey.Select(kv => new AttributeFilter(kv.Key, kv.Value)).ToList();

        void Add(string key, IEnumerable<string> values)
        {
            if (!byKey.TryGetValue(key, out var list))
                byKey[key] = list = [];
            foreach (var v in values)
                if (!list.Contains(v, StringComparer.OrdinalIgnoreCase))
                    list.Add(v);
        }
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
