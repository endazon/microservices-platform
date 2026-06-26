namespace KnowledgePlatform.Bff.Endpoints;

public static class AnalysisBffEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/bff/analysis").WithTags("Analysis BFF");
        // FR-07, UC-02: AI 分析依頼 (stub)
        group.MapPost("/ask", (AnalysisRequest request) =>
            Results.Accepted("/bff/analysis/sessions/stub-session-id",
                new { sessionId = "stub-session-id", status = "processing" }))
            .WithName("BffAnalysisAsk");
    }
}

public record AnalysisRequest(string Question, string? Scope = null);
