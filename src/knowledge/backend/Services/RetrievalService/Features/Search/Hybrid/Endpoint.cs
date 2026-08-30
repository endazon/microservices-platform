using System.Diagnostics;
using Knowledge.Contracts.Dtos;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 を RRF で統合 + ABAC フィルタ）。
//
// ADR-0068 決定 1: 端点はこの操作の処理であり 3 段目に置く。`MapGroup` とタグ付けは
// 集約の登録表（`SearchEndpoints`）に残る。
//
// 二段検索（ADR-0035）は同じ `IHybridSearchService` の着脱可能な段であって別の操作ではない
// ——本フォルダの `HybridSearchService` / `GraphExpandingSearchService` / `GraphRerank` /
// `GraphExpansionOptions` はいずれもこの操作だけが使う（ADR-0068 決定 2）。
internal static class SearchEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (SearchRequest req, IHybridSearchService search, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var results = await search.SearchAsync(req, ct);
            sw.Stop();
            return Results.Ok(new SearchResponse(results, results.Count, sw.ElapsedMilliseconds));
        }).WithName("Search").Produces<SearchResponse>();
    }
}
