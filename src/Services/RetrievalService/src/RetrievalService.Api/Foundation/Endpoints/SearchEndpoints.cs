using KnowledgePlatform.Shared.Contracts.Dtos;
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

        return app;
    }
}
