using System.Diagnostics;
using Knowledge.Contracts.Dtos;
using RetrievalService.Domain.Ports;

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
        g.MapPost("/", async (SearchRequest req, IHybridSearchService search,
            ISearchAccessResolver access, HttpContext http, CancellationToken ct) =>
        {
            // 🔴 FR-05, NFR-09, ADR-0034 決定 1, [[IADR-0410]], [[IADR-0416]] (#1339):
            // **権限の根拠は自分で引く。** 本文の `Scope` は呼び出し元の主張であり、
            // ここでは**絞り込み**としてしか効かない（狭める方向にのみ作用する）。
            // 正直な呼び出し元にとって結果は変わらない —— 送ってくるのは同じ許可か、それを絞ったものである。
            // 🔴 **呼び出し元が「解決していない」ことは今も deny のままである**（[[IADR-0012]]）。
            // 権威で上書きすると、`Scope` を送らない呼び出し元が**今日より広く**見えることになる
            // —— 信頼を外す変更が、緩む向きの変更を連れてきてはならない。
            // **変えるのは「主張を信じるか」だけであり、「主張が要るか」ではない。**
            if (req.Scope is not { GrantsAccess: true })
                return Results.Ok(new SearchResponse([], 0, 0));

            var effective = ScopeNarrowing.Apply(
                await access.ResolveAsync(http, ct), req.Scope);

            var sw = Stopwatch.StartNew();
            var results = await search.SearchAsync(req with { Scope = effective }, ct);
            sw.Stop();
            return Results.Ok(new SearchResponse(results, results.Count, sw.ElapsedMilliseconds));
        }).WithName("Search").Produces<SearchResponse>();
    }
}
