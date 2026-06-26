using KnowledgePlatform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Endpoints;

public static class SearchEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/search").WithTags("Search");
        // FR-03, UC-01: ハイブリッド検索スケルトン
        group.MapGet("/", (string? q, int limit = 10) =>
            Results.Ok(new SearchResultDto { Query = q ?? "" }))
            .WithName("Search").Produces<SearchResultDto>();
    }
}
