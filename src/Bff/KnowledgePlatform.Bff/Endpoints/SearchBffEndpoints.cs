using KnowledgePlatform.Shared.Contracts.Dtos;

namespace KnowledgePlatform.Bff.Endpoints;

public static class SearchBffEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/bff/search").WithTags("Search BFF");
        // FR-03, FR-04, UC-01: 横断検索 + AI 回答 (stub — P1 で RetrievalService + AiAnalysisService を呼ぶ)
        group.MapPost("/", (SearchRequest request) =>
            Results.Ok(new SearchResultDto
            {
                Query = request.Query,
                AiAnswer = "stub answer (P1 will call RetrievalService + AiAnalysisService)"
            }))
            .WithName("BffSearch").Produces<SearchResultDto>();
    }
}

public record SearchRequest(string Query, int Limit = 10);
