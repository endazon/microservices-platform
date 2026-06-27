using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（/complete）
public static class CompletionEndpoints
{
    public static IEndpointRouteBuilder MapCompletionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Completions");

        // AiAnalysisService が POST /complete で呼び出す
        g.MapPost("/complete", async (CompletionApiRequest req, ILlmProvider llm, CancellationToken ct) =>
        {
            var result = await llm.CompleteAsync(
                new CompletionRequest(req.Prompt, req.MaxTokens, req.Model), ct);
            return Results.Ok(new
            {
                result.Text,
                result.InputTokens,
                result.OutputTokens,
                Model = req.Model ?? "claude-sonnet-4-6"
            });
        }).WithName("Complete").Produces<CompletionResult>();

        return app;
    }
}

public record CompletionApiRequest(string Prompt, int MaxTokens = 1024, string? Model = null);
