using AiAnalysisService.Api.Services;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Endpoints;

// FR-04, FR-07, UC-01, UC-02: AI 分析・回答エンドポイント
public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/analysis").WithTags("Analysis");

        // FR-04, UC-01: RAG 質問回答
        g.MapPost("/ask", async (AskRequest req, IRagOrchestrator rag,
            HttpContext http) =>
        {
            // JWT から userId を取得（テスト環境では anonymous を使用）
            var userId = http.User.Identity?.Name ?? "anonymous";
            var userAttrs = ExtractUserAttributes(http);
            var answer = await rag.AskAsync(req.Question, userId, userAttrs);
            return Results.Ok(answer);
        }).WithName("Ask").Produces<AiAnswerDto>();

        return app;
    }

    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }
}

public record AskRequest(string Question, string? Scope = null);
