using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

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
            .WithName("Embed")
            .Produces<EmbedApiResponse>()
            // 🔴 NFR-09, ADR-0004, ADR-0084 決定 1, [[IADR-0379]] 決定 4, [[IADR-0424]] (#1364):
            // **この端点は `ServiceCaller` を要する。** 従前は「サービス間呼び出し専用だから」という
            // 理由で認可を掛けていなかったが、**専用であることと誰でも通すことは同じではない**
            // （`AuthzEndpoints` が #1333 で同じ誤りを正した形と同型である）。
            // メッシュの mTLS は相手の身元を保証するだけで、身元を見る処理はここに無かった。
            //
            // 🔴 **群ではなく端点へ掛ける**（`ADR-0084` 決定 1）。群 `MapGroup("")` はタグ付けのためにあり、
            // 端点が増えたときに**新しい端点が黙って門を継承する**形にしない。
            .RequireAuthorization(PlatformAuthPolicies.ServiceCaller);

        return app;
    }
}
