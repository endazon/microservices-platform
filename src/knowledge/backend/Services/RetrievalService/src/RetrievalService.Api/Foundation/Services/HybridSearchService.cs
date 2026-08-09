using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
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

        // FR-03, SC-02, #531: 検索モード（3 値）で使う系統を選ぶ。未知・未指定は hybrid へ縮退する。
        var mode = SearchModes.Normalize(request.Mode);

        // FR-03, SC-02, #532: 並び順（2 値）。未知・未指定は既定（relevance）へ縮退する。
        // **取得後に並べ替える**（IADR-0150 決定 1）——関連度が候補を決め、日時は表示順だけを決める。
        var sort = SearchSorts.Normalize(request.SortBy);

        // 単系統のときは候補を広げる意味が無い（融合しないため）ので topK をそのまま使う。
        // **ただし日時順のときは広げる**（IADR-0150 決定 3）——並べ替える以上、候補が広いほど
        // 「もっと新しい関連文書」を拾える。融合と同じ幅に揃える（同じ検索で 2 つの候補幅を持たない）。
        var singleModeK = sort == SearchSorts.Updated ? candidateK : request.TopK;

        if (mode == SearchModes.Keyword)
            return Finish(await store.KeywordSearchAsync(request.Query, singleModeK, filters, ct),
                sort, request.TopK);

        if (mode == SearchModes.Semantic)
        {
            var semanticVector = await embed.EmbedAsync(request.Query, ct);
            return Finish(await store.SearchAsync(semanticVector, singleModeK, filters, ct),
                sort, request.TopK);
        }

        // FR-03: 意味検索（ベクトル）と全文検索（キーワード）を並行実行し p95 を抑える
        var vector = await embed.EmbedAsync(request.Query, ct);
        var vectorTask = store.SearchAsync(vector, candidateK, filters, ct);
        var keywordTask = store.KeywordSearchAsync(request.Query, candidateK, filters, ct);
        await Task.WhenAll(vectorTask, keywordTask);

        // FR-03: 順位ベースで両系統を統合（スコアのスケール差を正規化なしで吸収）
        var fused = ReciprocalRankFusion(vectorTask.Result, keywordTask.Result);
        return Finish(fused, sort, request.TopK);
    }

    // FR-03, SC-02, #532: 並び順を適用して topK 件へ切る（IADR-0150 決定 1・3・4）。
    //
    // **relevance（既定）は取得順のまま**——現行の振る舞いを一切変えない。
    // **updated は更新日時の降順**で、**日時を持たないチャンク（未再索引・IADR-0149 決定 3）は末尾**へ置く。
    // 「新しい順」の先頭が日時不明で埋まらないようにするためであり、**DateTimeOffset.MinValue へ
    // 倒さない**——倒すと比較子が嘘を持ち、「本当に古い文書」と区別できなくなる。
    //
    // **OrderByDescending は安定ソート**である（.NET の保証）。同着（同じ日時・日時なし同士）は
    // 元の順序＝関連度の順を保つ。
    internal static List<SearchResultDto> Finish(
        List<SearchResultDto> results, string sort, int topK)
    {
        if (sort != SearchSorts.Updated)
            return results.Take(topK).ToList();

        return results
            .OrderByDescending(r => r.UpdatedAt.HasValue)   // 日時なしを末尾へ（null-last を明示する）
            .ThenByDescending(r => r.UpdatedAt ?? default)  // 日時ありの中で新しい順
            .Take(topK)
            .ToList();
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
