using AiAnalysisService.Domain;
using AiAnalysisService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Features.Analysis.Analyze;

// FR-07, UC-02: 指定データ範囲での分析・比較・抽出。
internal static class AnalyzeEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/analyze", async (AnalysisTaskRequest req, IRagOrchestrator rag,
            HttpContext http) =>
        {
            // FR-07: 指示は必須。空依頼は受け付けない（バリデーション）。
            if (string.IsNullOrWhiteSpace(req.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });

            // FR-07: プロンプトインジェクション緩和。過大な指示は受け付けない。
            if (req.Instruction.Length > AnalysisPromptBuilder.MaxInstructionLength)
                return Results.BadRequest(new
                {
                    error = $"instruction must be {AnalysisPromptBuilder.MaxInstructionLength} characters or fewer"
                });

            // FR-05: JWT から利用者を特定し、権限解決（範囲は権限を広げない）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = AnalysisEndpoints.ExtractUserAttributes(http);
            var answer = await rag.AnalyzeAsync(req, userId, userAttrs);
            return Results.Ok(answer);
        }).WithName("Analyze").Produces<AiAnswerDto>();
    }
}
