using System.Diagnostics;
using Platform.Shared.Contracts.Dtos;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Features.Completions.Complete;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（POST /complete）。
// FR-11: 入力の機密区分・用途に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替える。
public static class CompleteEndpoint
{
    public static IEndpointRouteBuilder MapComplete(this IEndpointRouteBuilder app)
    {
        // AiAnalysisService が POST /complete で呼び出す
        app.MapPost("/complete", async (
            CompletionApiRequest req,
            ILlmRouter router,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            LlmCompletionMetrics metrics,
            LlmUsageMetrics usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.Complete");
            // NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視のトラフィックか。
            // 🔴 **本サービスはメッシュ内部の面である**（外部から到達しない）。標識は外周（BFF）が
            // 検証済み JWT の主体から決めて付けたヘッダであり、ここでは引き継ぐだけである。
            var isSynthetic = SyntheticTraffic.IsSyntheticInternalRequest(http.Request);

            // FR-11: 越境マトリクスで送信先を判定する。
            var sensitivity = SensitivityClasses.Parse(req.Confidentiality);
            var purpose = string.IsNullOrWhiteSpace(req.Purpose) ? "default" : req.Purpose!;
            var decision = router.Route(new RoutingRequest(sensitivity, purpose, req.Model));

            if (!decision.Allowed)
            {
                // 送信拒否（縮退）。呼び出し側が出典のみ返す等の縮退へ切り替えられるよう Sent=false を返す。
                // IADR-0110: 未送信も計上する（分母が欠けると拒否率が過大に見える）。
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultEgressDenied, null, decision, purpose, sensitivity);
                return Results.Ok(new CompletionApiResponse(
                    Text: decision.Reason, Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: null, RoutingReason: decision.Reason));
            }

            var provider = services.GetKeyedService<ILlmProvider>(decision.Provider);
            if (provider is null)
            {
                logger.LogError("Provider not registered: {Provider} (endpoint {Endpoint})",
                    decision.Provider, decision.EndpointName);
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultProviderMissing, null, decision, purpose, sensitivity);
                return Results.Ok(new CompletionApiResponse(
                    Text: $"呼び出し先プロバイダ {decision.Provider} が未登録のため送信できません。",
                    Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }

            // FR-11, ADR-0038 決定 3 (#863): 第 1 候補 → フォールバック順序 の順に試す。
            // 鎖が空なら 1 回だけ回り、従来と同じ挙動になる（回帰なし）。
            var chain = new List<string?> { decision.Model };
            chain.AddRange(decision.Fallbacks);

            for (var attemptIndex = 0; attemptIndex < chain.Count; attemptIndex++)
            {
                // 実際に投げたモデルで応答・メトリクス・ログを名乗る（IADR-0111: 使用モデルを偽らない）。
                var attempt = decision with { Model = chain[attemptIndex] };
                try
                {
                    var result = await provider.CompleteAsync(
                        new CompletionRequest(req.Prompt, req.MaxTokens, attempt.Model), ct);
                    CompletionEndpoints.LogStopReason(logger, result.StopReason, attempt);
                    // IADR-0110: 越境が成立した呼び出し（拒否率の分母）。終了理由は別属性で載せる。
                    metrics.RecordCompletion(
                        LlmCompletionMetrics.ResultSent, result.StopReason, attempt, purpose, sensitivity,
                        result.OutputTokens);
                    // FR-10, ADR-0044 決定 1・3 (#443): 用途別・モデル別のトークン累計と金額換算。
                    // **実際に投げたモデル（attempt）で計上する** —— フォールバックが起きた呼び出しの
                    // 費用は第 1 候補ではなく成功した候補の単価で発生する。
                    //
                    // NFR-02, ADR-0076 決定 4, [[IADR-0378]] (#1203): 🔴 **合成監視は費用へ計上しない。**
                    // 監視のために打った呼び出しが費用に入ると、費用が「人が使った量」を表さなくなる。
                    // **黙って落とさず、除外した件数を別の計器に積む。**
                    if (isSynthetic)
                        usage.RecordSyntheticExclusion(attempt, purpose, sensitivity);
                    else
                        usage.RecordUsage(attempt, purpose, sensitivity, result.InputTokens, result.OutputTokens);
                    return Results.Ok(new CompletionApiResponse(
                        result.Text, attempt.Model ?? string.Empty, result.InputTokens, result.OutputTokens,
                        Sent: true, Endpoint: attempt.EndpointName, RoutingReason: attempt.Reason,
                        StopReason: result.StopReason));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ADR-0038 決定 4 (#863): フォールバックするのは HTTP 400 系のときだけである。
                    // **429 は再試行であってフォールバックではない**ため、ここでは落とさない。
                    var hasNextCandidate = attemptIndex + 1 < chain.Count;
                    if (hasNextCandidate && LlmFallbackPolicy.ShouldFallBack(ex))
                    {
                        // ADR-0038 決定 6: 発火を可観測にする。メトリクスは llm.result=fallback（llm.model は
                        // 見送った候補）、ログには遷移と上流ステータスを残す。**利用者由来の purpose は
                        // 載せない**（設定由来のモデル名・エンドポイント名だけを載せ、ログ行偽造の経路を作らない）。
                        // IADR-0374 (#1091): 見送った試行に対して上流が返したものも軸として残す。
                        // 発火は 400 系に限り 429 を除くため、この行の値は構成上 client_error に限られる
                        // （系列は増えない）。「この試行に上流が何を返したか」を全行で真にしておく。
                        metrics.RecordCompletion(
                            LlmCompletionMetrics.ResultFallback, null, attempt, purpose, sensitivity,
                            failure: ex);
                        logger.LogWarning(ex,
                            "LLM falling back at endpoint {Endpoint}: {FromModel} -> {ToModel} (upstream status {Status})",
                            attempt.EndpointName, attempt.Model, chain[attemptIndex + 1],
                            LlmFallbackPolicy.StatusCodeOf(ex));
                        continue;
                    }

                    // 呼び出し先が不調な場合も 500 を伝播させず、縮退可能な応答を返す。
                    // IADR-0374 (#1091): **429 が来るのはここである。** 従前この経路は上流ステータスを
                    // ログにもメトリクスにも残しておらず、429 と 5xx・通信断が upstream_error の一点へ
                    // 潰れていた（フォールバックした側だけが観測できるという非対称）。両方へ残す。
                    logger.LogError(ex, "LLM call failed at endpoint {Endpoint} ({Model}) (upstream status {Status})",
                        attempt.EndpointName, attempt.Model, LlmFallbackPolicy.StatusCodeOf(ex));
                    metrics.RecordCompletion(
                        LlmCompletionMetrics.ResultUpstreamError, null, attempt, purpose, sensitivity,
                        failure: ex);
                    return Results.Ok(new CompletionApiResponse(
                        Text: $"呼び出し先 {attempt.EndpointName} が現在利用できません。",
                        Model: attempt.Model ?? string.Empty, InputTokens: 0, OutputTokens: 0,
                        Sent: false, Endpoint: attempt.EndpointName, RoutingReason: attempt.Reason));
                }
            }

            // 到達しない（ループは必ず return する）。コンパイラの制御フロー解析のための保険。
            throw new UnreachableException("completion attempt chain ended without a response");
        }).WithName("Complete").Produces<CompletionApiResponse>();

        return app;
    }
}
