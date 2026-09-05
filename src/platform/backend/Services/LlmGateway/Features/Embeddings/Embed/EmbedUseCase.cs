using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Features.Embeddings.Embed;

// FR-02, FR-03, FR-05, NFR-09, ADR-0013, ADR-0016, ADR-0017, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255):
// 埋め込み生成の**判定器本体**。REST（EmbeddingEndpoints）と gRPC（GrpcService）の**両方がここを呼ぶ**。
//
// 🔴 **判定器を 2 つにしない**（IADR-0379 決定 5 と同じ向き。参照実装が REST と gRPC で
// `AbacEvaluator.ResolveScope` を共有したのと同型）。越境判定・プロバイダ解決・次元照合・上流不調の
// 4 つの縮退はすべてこの中に閉じ、輸送（HTTP / gRPC）は写像だけを持つ。ここを分けると、
// **どちらか一方だけが機密区分の fail-closed を通る**という最悪の食い違いが起こり得る。
//
// 縮退はすべて `Embedded=false` の**応答**で表す（例外にしない）。REST では 200 ＋ Embedded=false、
// gRPC では OK ＋ embedded=false であり、意味は同じである。
public sealed class EmbedUseCase(
    IEmbeddingRouter router,
    IServiceProvider services,
    ILoggerFactory loggerFactory)
{
    public const string LoggerCategory = "LlmGateway.Embed";

    public async Task<EmbedApiResponse> ExecuteAsync(EmbedApiRequest req, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(LoggerCategory);

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
            return new EmbedApiResponse(
                Vector: [], Dimensions: 0, Model: string.Empty, Collection: string.Empty,
                Embedded: false, Endpoint: null, RoutingReason: decision.Reason, Retryable: false);
        }

        var provider = services.GetKeyedService<IEmbeddingProvider>(decision.Provider);
        if (provider is null)
        {
            // 構成不備（プロバイダ未登録）は自動リトライで解消しないため恒久扱い（Retryable=false）。
            logger.LogError("Embedding provider not registered: {Provider} (endpoint {Endpoint})",
                decision.Provider, decision.EndpointName);
            return new EmbedApiResponse(
                Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                Embedded: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason,
                Retryable: false);
        }

        try
        {
            var vector = await provider.EmbedAsync(req.Text, decision.Model, decision.Dimensions, purpose, ct);

            // FR-02: 次元不整合のベクトルはモデル別コレクションの次元と一致しないため索引しない（fail-closed）。
            // 次元不整合はモデル/設定の不一致であり自動リトライで解消しないため恒久扱い（Retryable=false）。
            if (vector.Length != decision.Dimensions)
            {
                logger.LogError(
                    "Embedding dimension mismatch at {Endpoint}: expected {Expected} got {Actual}",
                    decision.EndpointName, decision.Dimensions, vector.Length);
                return new EmbedApiResponse(
                    Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                    Embedded: false, Endpoint: decision.EndpointName,
                    RoutingReason: $"埋め込み次元不整合（期待 {decision.Dimensions} / 実際 {vector.Length}）",
                    Retryable: false);
            }

            return new EmbedApiResponse(
                Vector: vector, Dimensions: vector.Length, Model: decision.Model,
                Collection: decision.Collection, Embedded: true,
                Endpoint: decision.EndpointName, RoutingReason: decision.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 送信先が不調・未設定でも 500 を伝播させず、索引スキップ可能な応答へ縮退する。
            // ただしこれは「一時的な障害」であり fail-closed（意図的拒否）とは区別する。Retryable=true を返し、
            // 呼び出し側（Ingestion）は恒久スキップではなく再試行（MassTransit リトライ/DLQ）に回す。
            // 一括再索引中に Voyage が一時不調でもチャンクを取りこぼさないための担保（Issue #98 レビュー対応）。
            logger.LogError(ex, "Embedding call failed at endpoint {Endpoint} ({Model})",
                decision.EndpointName, decision.Model);
            return new EmbedApiResponse(
                Vector: [], Dimensions: 0, Model: decision.Model, Collection: decision.Collection,
                Embedded: false, Endpoint: decision.EndpointName,
                RoutingReason: $"送信先 {decision.EndpointName} が現在利用できません。", Retryable: true);
        }
    }
}
