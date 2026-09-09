using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Features.Completions.Complete;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（POST /complete）。
// FR-11: 入力の機密区分・用途に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替える。
//
// IADR-0400 (#1255): 判定の本体は CompletionUseCase にある。**gRPC 面（GrpcService）が同じ本体を呼ぶ** ——
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
        })
            .WithName("Complete")
            .Produces<CompletionApiResponse>()
            // 🔴 NFR-09, ADR-0004, ADR-0084 決定 1, [[IADR-0379]] 決定 4, [[IADR-0424]] (#1364):
            // **この端点は `ServiceCaller` を要する。** 従前は認可を 1 つも持たず、
            // `AddPlatformAuth`（器の登録）と `FallbackPolicy` 不在のため**誰でも叩けた** ——
            // LLM 呼び出しは課金を伴い、メッシュの mTLS は相手の身元を保証するだけで
            // 「その身元を見る」処理がここに無かった。
            //
            // 🔴 **門は端点ごとに掛ける**（`ADR-0084` 決定 1 の補完節: `FallbackPolicy` は門ではない。
            // 端点または端点群に付くものだけを門とする）。
            // 🔴 **利用者のトークンでは通さない** —— 通すと呼び出し先が「利用者が直接呼んだ」と
            // 区別できず confused deputy になる（gRPC 面と同じ 1 つのポリシーである）。
            .RequireAuthorization(PlatformAuthPolicies.ServiceCaller);

        return app;
    }
}
