using AiAnalysisService.Domain;
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
            // 🔴 FR-05, [[IADR-0335]] 決定 4 (#1318): **未認証は認可サービスを呼ばずに倒す。**
            // 判定と理由は `AnalysisEndpoints.IsAnonymous` に 1 つだけ置く。
            if (AnalysisEndpoints.IsAnonymous(http))
                return Results.Ok(NoAccessAnswer.Answer());

            // JWT から userId を取得する。**ここへ到達するのは認証済みの要求だけである。**
            var userId = http.User.Identity!.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);
            var answer = await rag.AskAsync(req.Question, userId, userAttrs, req.AttributeFilters);
            return Results.Ok(answer);
        }).WithName("Ask").Produces<AiAnswerDto>();
    }
}
