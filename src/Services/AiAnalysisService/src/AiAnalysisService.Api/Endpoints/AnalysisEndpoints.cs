namespace AiAnalysisService.Api.Endpoints;

public static class AnalysisEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/analysis").WithTags("Analysis");
        // FR-04, FR-07, UC-01, UC-02: AI 回答・分析スケルトン
        group.MapPost("/ask", (AskRequest request) =>
            Results.Ok(new { answer = "stub answer (P1 implements RAG)", sources = Array.Empty<string>() }))
            .WithName("Ask");
        group.MapGet("/sessions/{id:guid}", (Guid id) =>
            Results.NotFound(new { message = $"Session {id} not found (stub)" }))
            .WithName("GetSession").Produces(404);
    }
}

public record AskRequest(string Question, string? Scope = null);
