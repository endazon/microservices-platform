using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Foundation.Ports;
using System.Diagnostics;

namespace RetrievalService.Api.Foundation.Endpoints;

// FR-03, UC-01: ハイブリッド検索エンドポイント（ベクトル検索 + ABAC フィルタ）
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/search").WithTags("Search");

        // FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 を RRF で統合 + ABAC フィルタ）
        g.MapPost("/", async (SearchRequest req, IHybridSearchService search, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var results = await search.SearchAsync(req, ct);
            sw.Stop();
            return Results.Ok(new SearchResponse(results, results.Count, sw.ElapsedMilliseconds));
        }).WithName("Search").Produces<SearchResponse>();

        // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043・裁定 Q2）。
        //
        // **辞書を丸ごと返さない**（決定 1）——返すのは「**到達できる文書に実際に付与された値**」だけ。
        // **件数を返さない**（決定 2）——`AttributeValuesResponse` は値の配列しか持たない。
        // **検索と同じ ABAC フィルタを使う**（[[IADR-0151]] 決定 1）——別経路で数えると
        // 「検索には出るが候補に無い値」が生まれる。
        //
        // **スコープが解決できない（deny-by-default）ときは空配列**（[[IADR-0151]] 決定 5）。
        // 404 にも 403 にもしない——**候補が無いことと権限が無いことを区別させない**。
        g.MapPost("/attribute-values", async (
            AttributeValuesRequest req, IVectorStore store, CancellationToken ct) =>
        {
            // deny-by-default: BFF が解決できなかった（null）／許可なしのスコープでは何も返さない。
            if (req.Scope is not { GrantsAccess: true } scope || string.IsNullOrWhiteSpace(req.Key))
                return Results.Ok(new AttributeValuesResponse([]));

            // FR-19, IADR-0253 決定 1（段 3 / #989）: 分岐があれば選言で絞る。
            // **検索と同じ制約を渡す**（別経路で絞ると「検索には出るが候補に無い値」が生まれる。
            // IADR-0151 決定 1）。
            var values = await store.ListAttributeValuesAsync(
                AttributeValueKeys.ToPayloadKey(req.Key),
                new ScopeFilter(
                    scope.Filters,
                    scope.Branches is { Count: > 0 }
                        ? [.. scope.Branches.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)]
                        : null),
                ct);

            return Results.Ok(new AttributeValuesResponse(values));
        }).WithName("AttributeValues").Produces<AttributeValuesResponse>();

        return app;
    }
}
