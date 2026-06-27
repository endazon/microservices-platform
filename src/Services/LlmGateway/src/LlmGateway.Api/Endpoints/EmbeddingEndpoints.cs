using LlmGateway.Api.Providers;

namespace LlmGateway.Api.Endpoints;

// FR-03, ADR-0009, ADR-0013: 埋め込み生成エンドポイント（/embed）
public static class EmbeddingEndpoints
{
    public static IEndpointRouteBuilder MapEmbeddingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Embeddings");

        // IngestionService / RetrievalService が POST /embed で呼び出す
        g.MapPost("/embed", async (EmbedRequest req, ILlmProvider llm, CancellationToken ct) =>
        {
            var vector = await llm.EmbedAsync(req.Text, ct);
            return Results.Ok(new { Vector = vector, Dimensions = vector.Length });
        }).WithName("Embed");

        return app;
    }
}

public record EmbedRequest(string Text);
