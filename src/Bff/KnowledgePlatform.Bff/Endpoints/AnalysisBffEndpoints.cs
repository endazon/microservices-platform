namespace KnowledgePlatform.Bff.Endpoints;

// FR-07, UC-02: BFF AI 分析集約エンドポイント
public static class AnalysisBffEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/analysis").WithTags("Analysis BFF");

        // Phase 2: AiAnalysisService を呼び出す
        g.MapPost("/ask", (AnalysisRequest req) =>
            Results.Accepted("/bff/analysis/sessions/stub",
                new { sessionId = "stub", status = "processing" }))
            .WithName("BffAnalysisAsk");

        return app;
    }
}

public record AnalysisRequest(string Question, string? Scope = null);
