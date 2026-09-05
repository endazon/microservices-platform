using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Features.Completions.Complete;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（POST /complete）。
// FR-11: 入力の機密区分・用途に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替える。
//
// IADR-0398 (#1255): 判定の本体は CompletionUseCase にある。**gRPC 面（GrpcService）が同じ本体を呼ぶ** ——
// ここに判定を戻すと、輸送ごとに越境判定・フォールバック鎖・計器の計上が分かれる（判定器を 2 つにしない）。
public static class CompleteEndpoint
{
    public static IEndpointRouteBuilder MapComplete(this IEndpointRouteBuilder app)
    {
        // AiAnalysisService が POST /complete で呼び出す
        app.MapPost("/complete", async (
            CompletionApiRequest req,
            CompletionUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            // NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視のトラフィックか。
            // 🔴 **本サービスはメッシュ内部の面である**（外部から到達しない）。標識は外周（BFF）が
            // 検証済み JWT の主体から決めて付けたヘッダであり、ここでは引き継ぐだけである。
            // 判定は単一情報源（SyntheticTraffic）にあり、gRPC 面は同じ関数を
            // ServerCallContext.GetHttpContext().Request から呼ぶ。
            var isSynthetic = SyntheticTraffic.IsSyntheticInternalRequest(http.Request);
            return Results.Ok(await useCase.ExecuteAsync(req, isSynthetic, ct));
        }).WithName("Complete").Produces<CompletionApiResponse>();

        return app;
    }
}
