using KnowledgePlatform.Shared.Contracts.Dtos;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-03, FR-04, UC-01: BFF 検索集約エンドポイント
public static class SearchBffEndpoints
{
    public static IEndpointRouteBuilder MapSearchBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/search").WithTags("Search BFF");

        // 後段で AiAnalysisService を呼び出す（Phase 2 で実装）
        g.MapPost("/", (SearchRequest req) =>
            Results.Ok(new SearchResponse([], 0, 0)))
            .WithName("BffSearch").Produces<SearchResponse>();

        return app;
    }
}
