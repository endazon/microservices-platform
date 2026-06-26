using KnowledgePlatform.Shared.Contracts.Dtos;
using RetrievalService.Api.Abstractions;
using System.Diagnostics;

namespace RetrievalService.Api.Endpoints;

// FR-03, UC-01: ハイブリッド検索エンドポイント（ベクトル検索 + ABAC フィルタ）
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/search").WithTags("Search");

        // FR-03: 意味検索（ベクトル + 属性フィルタ）
        g.MapPost("/", async (SearchRequest req, IVectorStore store, IEmbeddingService embed) =>
        {
            var sw = Stopwatch.StartNew();
            var vector = await embed.EmbedAsync(req.Query);
            var results = await store.SearchAsync(vector, req.TopK, req.AttributeFilters);
            sw.Stop();
            return Results.Ok(new SearchResponse(results, results.Count, sw.ElapsedMilliseconds));
        }).WithName("Search").Produces<SearchResponse>();

        return app;
    }
}
