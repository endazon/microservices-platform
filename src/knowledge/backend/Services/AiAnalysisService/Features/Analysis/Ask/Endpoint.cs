using AiAnalysisService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Features.Analysis.Ask;

// FR-04, UC-01: RAG 質問回答。
internal static class AskEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/ask", async (AskRequest req, IRagOrchestrator rag,
            HttpContext http) =>
        {
            // JWT から userId を取得（テスト環境では anonymous を使用）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);
            var answer = await rag.AskAsync(req.Question, userId, userAttrs, req.AttributeFilters);
            return Results.Ok(answer);
        }).WithName("Ask").Produces<AiAnswerDto>();
    }
}
