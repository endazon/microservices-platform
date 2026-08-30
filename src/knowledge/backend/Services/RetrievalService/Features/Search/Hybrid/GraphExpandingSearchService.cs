using RetrievalService.Domain;
using Knowledge.Contracts.Dtos;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.Hybrid;

// FR-04, FR-14, FR-17, UC-10, ADR-0035 決定 1・2, ADR-0018 (#970): **二段検索の段。**
//
// ```text
// ① 既存のハイブリッド検索                        （HybridSearchService。実装は変えていない）
// ② ①の**ベクトル側**上位 N 件を起点にグラフ近傍展開（IGraphNeighborExpander）
// ③ ②で到達した文書 ID に絞ったベクトル検索        （IVectorStore.SearchWithinDocumentsAsync）
// ④ ① と ③ を重みつき合成で再ランク               （GraphRerank）
// ```
//
// 🔴 **本クラスは「着脱可能な段」そのものである**（ADR-0018 / FR-14）。既定オフは
// **フラグで中を分岐させるのではなく、DI にこの型を登録しないこと**で実現する
// （`Program.cs`）。段が無い構成では `IHybridSearchService` は素の `HybridSearchService` であり、
// **本ファイルのコードは 1 行も実行経路に存在しない**。A/B 比較はこの差で行う。
//
// 🔴 **`CitationDto` の契約を一切変えない。** グラフ由来の候補も段③を通ることで
// ChunkId / Score / Snippet を**正規の経路で**持つ（文書単位のノードのままでは出典にできない）。
public sealed class GraphExpandingSearchService(
    HybridSearchService inner,
    IVectorStore store,
    IGraphNeighborExpander expander,
    GraphExpansionOptions options,
    ILogger<GraphExpandingSearchService> logger)
    : IHybridSearchService
{
    public async Task<List<SearchResultDto>> SearchAsync(
        SearchRequest request, CancellationToken ct = default)
    {
        // ① 既存のハイブリッド検索。**ここまでは段が無いときと完全に同じ。**
        var outcome = await inner.SearchDetailedAsync(request, ct);

        // ② 起点は**ベクトル側**上位 N 件の文書だけ（ADR-0035 決定 2）。
        //    全文検索側のヒットは起点にしない —— 語の一致だけで辿り始めると、
        //    グラフ展開が「たまたま同じ語を含む文書」から芋づる式に広がる。
        var seeds = outcome.VectorSide
            .Select(r => r.DocumentId)
            .Distinct()
            .Take(options.SeedCount)
            .ToList();

        if (seeds.Count == 0 || outcome.QueryVector.Length == 0)
            return HybridSearchService.Finish(outcome.Fused, outcome.Sort, outcome.TopK);

        var neighborhood = await expander.ExpandAsync(seeds, options.Hops, ct);
        var proximity = GraphProximity.From(seeds, neighborhood.Edges, options.Hops);

        // 🔴 **グラフが 0 件なら段③を呼ばない。** 呼んでも #969 の口は「空集合＝該当なし」で
        // 空を返すが、**呼ばないことをここでも明示する** —— 「空を全件と読む」誤りが
        // 生きるのは 2 か所（ポートの実装と、その呼び出し側）だからである。
        if (proximity.Count == 0)
            return HybridSearchService.Finish(outcome.Fused, outcome.Sort, outcome.TopK);

        // ③ 文書 ID を絞ったベクトル検索。**ABAC フィルタは AND で重なる**（IADR-0259 決定 3）——
        //    グラフ側が誤って権限外の文書を返しても、ここで落ちる（多層防御）。
        var expanded = await store.SearchWithinDocumentsAsync(
            outcome.QueryVector, outcome.CandidateK, proximity.Keys.ToList(), outcome.Filters, ct);

        logger.LogDebug(
            "Graph expansion: {Seeds} seeds, {Reached} reached documents, {Chunks} chunks re-ranked",
            seeds.Count, proximity.Count, expanded.Count);

        // ④ 再ランク。**近接度は `Score` に混ざらない**（GraphRerank）。
        var merged = GraphRerank.Merge(
            outcome.Fused, expanded, proximity, options.SearchWeight, options.GraphWeight);

        return HybridSearchService.Finish(merged, outcome.Sort, outcome.TopK);
    }
}
