using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

public static class CompletionEndpoints
{
    // FR-04, ADR-0010: LLM テキスト生成スケルトン
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/completions").WithTags("Completions");
        group.MapPost("/", async (CompletionRequest request, ILlmProvider llm, CancellationToken ct) =>
        {
            var result = await llm.CompleteAsync(request, ct);
            return Results.Ok(result);
        }).WithName("Complete").Produces<CompletionResult>();
    }
}
