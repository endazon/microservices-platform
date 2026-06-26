using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

public static class EmbeddingEndpoints
{
    // FR-03, ADR-0009: テキスト埋め込みスケルトン（P1: Qdrant 連携）
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/embeddings").WithTags("Embeddings");
        group.MapPost("/", async (EmbedRequest request, ILlmProvider llm, CancellationToken ct) =>
        {
            var vector = await llm.EmbedAsync(request.Text, ct);
            return Results.Ok(new { vector, dimensions = vector.Length });
        }).WithName("Embed");
    }
}

public record EmbedRequest(string Text);
