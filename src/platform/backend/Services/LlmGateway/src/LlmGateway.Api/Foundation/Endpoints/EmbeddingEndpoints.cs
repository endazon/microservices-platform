using Platform.Shared.Contracts.Dtos;
using LlmGateway.Api.Foundation.Ports;
using LlmGateway.Api.Foundation.Routing;

namespace LlmGateway.Api.Foundation.Endpoints;

// FR-02, FR-03, FR-05, ADR-0013, ADR-0016, ADR-0017: 埋め込み生成エンドポイント（/embed）。
// 機密区分・用途に応じて送信先（ティア/エンドポイント/モデル/コレクション）を切り替える。
// confidential/restricted は EmbeddingRouter がティアA（セルフホスト）固定とし、無効なら fail-closed で
// 外部へ本文を送らず索引もしない（呼び出し側が Embedded=false でスキップ）。
public static class EmbeddingEndpoints
{
    public static IEndpointRouteBuilder MapEmbeddingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Embeddings");

        // IngestionService（Purpose=Index）/ RetrievalService（Purpose=Query）が POST /embed で呼び出す。
        g.MapPost("/embed", async (
            EmbedApiRequest req,
            IEmbeddingRouter router,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.Embed");

            // FR-05: 越境マトリクス（埋め込み専用・EmbeddingEgress）で送信先を判定する。
            var sensitivity = SensitivityClasses.Parse(req.Confidentiality);
            var purpose = req.Purpose == EmbedPurpose.Query
                ? EmbeddingRoutePurpose.Query
                : EmbeddingRoutePurpose.Index;
            var decision = router.Route(new EmbeddingRoutingRequest(sensitivity, purpose));

            if (!decision.Allowed)
            {
                // fail-closed: 送信拒否。空ベクトル・Embedded=false を返し、呼び出し側が索引をスキップする。
                // これは意図的・恒久的な拒否（機密区分×ティア）のため Retryable=false（再試行しない）。
                return Results.Ok(new EmbedApiResponse(
                    Vector: [], Dimensions: 0, Model: string.Empty, Collection: string.Empty,
                    Embedded: false, Endpoint: null, RoutingReason: decision.Reason, Retryable: false));
            }

            var provider = services.GetKeyedService<IEmbeddingProvider>(decision.Provider);
            if (provider is null)
            {
                // 構成不備（プロバイダ未登録）は自動リトライで解消しないため恒久扱い（Retryable=false）。
                logger.LogError("Embedding provider not registered: {Provider} (endpoint {Endpoint})",
                    decision.Provider, decision.EndpointName);
                return Results.Ok(new EmbedApiResponse(
                    Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                    Embedded: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason,
                    Retryable: false));
            }

            try
            {
                var vector = await provider.EmbedAsync(req.Text, decision.Model, decision.Dimensions, ct);

                // FR-02: 次元不整合のベクトルはモデル別コレクションの次元と一致しないため索引しない（fail-closed）。
                // 次元不整合はモデル/設定の不一致であり自動リトライで解消しないため恒久扱い（Retryable=false）。
                if (vector.Length != decision.Dimensions)
                {
                    logger.LogError(
                        "Embedding dimension mismatch at {Endpoint}: expected {Expected} got {Actual}",
                        decision.EndpointName, decision.Dimensions, vector.Length);
                    return Results.Ok(new EmbedApiResponse(
                        Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                        Embedded: false, Endpoint: decision.EndpointName,
                        RoutingReason: $"埋め込み次元不整合（期待 {decision.Dimensions} / 実際 {vector.Length}）",
                        Retryable: false));
                }

                return Results.Ok(new EmbedApiResponse(
                    Vector: vector, Dimensions: vector.Length, Model: decision.Model,
                    Collection: decision.Collection, Embedded: true,
                    Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 送信先が不調・未設定でも 500 を伝播させず、索引スキップ可能な応答へ縮退する。
                // ただしこれは「一時的な障害」であり fail-closed（意図的拒否）とは区別する。Retryable=true を返し、
                // 呼び出し側（Ingestion）は恒久スキップではなく再試行（MassTransit リトライ/DLQ）に回す。
                // 一括再索引中に Voyage が一時不調でもチャンクを取りこぼさないための担保（Issue #98 レビュー対応）。
                logger.LogError(ex, "Embedding call failed at endpoint {Endpoint} ({Model})",
                    decision.EndpointName, decision.Model);
                return Results.Ok(new EmbedApiResponse(
                    Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                    Embedded: false, Endpoint: decision.EndpointName,
                    RoutingReason: $"送信先 {decision.EndpointName} が現在利用できません。", Retryable: true));
            }
        }).WithName("Embed").Produces<EmbedApiResponse>();

        return app;
    }
}
