using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Features.Embeddings.Embed;

// FR-02, FR-03, FR-05, ADR-0013, ADR-0016, ADR-0017: 埋め込み生成エンドポイント（/embed）。
// 機密区分・用途に応じて送信先（ティア/エンドポイント/モデル/コレクション）を切り替える。
// confidential/restricted は EmbeddingRouter がティアA（セルフホスト）固定とし、無効なら fail-closed で
// 外部へ本文を送らず索引もしない（呼び出し側が Embedded=false でスキップ）。
//
// IADR-0397 (#1255): 判定の本体は EmbedUseCase にある。**gRPC 面（GrpcService）が同じ本体を呼ぶ** ——
// ここに判定を戻すと、輸送ごとに越境判定が分かれる（判定器を 2 つにしない）。
public static class EmbeddingEndpoints
{
    public static IEndpointRouteBuilder MapEmbeddingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Embeddings");

        // IngestionService（Purpose=Index）/ RetrievalService（Purpose=Query）が POST /embed で呼び出す。
        g.MapPost("/embed", async (
            EmbedApiRequest req,
            EmbedUseCase useCase,
            CancellationToken ct) => Results.Ok(await useCase.ExecuteAsync(req, ct)))
            .WithName("Embed").Produces<EmbedApiResponse>();

        return app;
    }
}
