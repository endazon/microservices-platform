using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Platform.Shared.Contracts.Dtos;
using LlmGateway.Api.Foundation.Observability;
using LlmGateway.Api.Foundation.Ports;
using LlmGateway.Api.Foundation.Routing;

namespace LlmGateway.Api.Foundation.Endpoints;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（/complete・/complete/stream）
// FR-11: 入力の機密区分・用途に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替える。
public static class CompletionEndpoints
{
    // SSE の data: 行に載せる JSON は Web 既定（camelCase）で直列化し、呼び出し側の JSON と揃える。
    // 日本語を \uXXXX へ過剰エスケープしないよう緩和エンコーダを用いる（SSE 本文の可読性・帯域）。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapCompletionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Completions");

        // AiAnalysisService が POST /complete で呼び出す
        g.MapPost("/complete", async (
            CompletionApiRequest req,
            ILlmRouter router,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            LlmCompletionMetrics metrics,
            LlmUsageMetrics usage,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.Complete");

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
                    LogStopReason(logger, result.StopReason, attempt);
                    // IADR-0110: 越境が成立した呼び出し（拒否率の分母）。終了理由は別属性で載せる。
                    metrics.RecordCompletion(
                        LlmCompletionMetrics.ResultSent, result.StopReason, attempt, purpose, sensitivity,
                        result.OutputTokens);
                    // FR-10, ADR-0044 決定 1・3 (#443): 用途別・モデル別のトークン累計と金額換算。
                    // **実際に投げたモデル（attempt）で計上する** —— フォールバックが起きた呼び出しの
                    // 費用は第 1 候補ではなく成功した候補の単価で発生する。
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
                        metrics.RecordCompletion(
                            LlmCompletionMetrics.ResultFallback, null, attempt, purpose, sensitivity);
                        logger.LogWarning(ex,
                            "LLM falling back at endpoint {Endpoint}: {FromModel} -> {ToModel} (upstream status {Status})",
                            attempt.EndpointName, attempt.Model, chain[attemptIndex + 1],
                            LlmFallbackPolicy.StatusCodeOf(ex));
                        continue;
                    }

                    // 呼び出し先が不調な場合も 500 を伝播させず、縮退可能な応答を返す。
                    logger.LogError(ex, "LLM call failed at endpoint {Endpoint} ({Model})",
                        attempt.EndpointName, attempt.Model);
                    metrics.RecordCompletion(
                        LlmCompletionMetrics.ResultUpstreamError, null, attempt, purpose, sensitivity);
                    return Results.Ok(new CompletionApiResponse(
                        Text: $"呼び出し先 {attempt.EndpointName} が現在利用できません。",
                        Model: attempt.Model ?? string.Empty, InputTokens: 0, OutputTokens: 0,
                        Sent: false, Endpoint: attempt.EndpointName, RoutingReason: attempt.Reason));
                }
            }

            // 到達しない（ループは必ず return する）。コンパイラの制御フロー解析のための保険。
            throw new UnreachableException("completion attempt chain ended without a response");
        }).WithName("Complete").Produces<CompletionApiResponse>();

        // IADR-0037: SSE ストリーミング版。AiAnalysisService が POST /complete/stream で呼び出す。
        // FR-11: egress ゲートは /complete と同一の router.Route(...) を通し、Allowed=false は
        // プロバイダを一切呼ばず理由イベントのみ返す（越境保証を弱めない）。
        g.MapPost("/complete/stream", async (
            CompletionApiRequest req,
            ILlmRouter router,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            LlmCompletionMetrics metrics,
            LlmUsageMetrics usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LlmGateway.CompleteStream");
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx でのバッファリング抑止

            async Task Send(CompletionStreamEvent ev)
            {
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, SseJson)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            // FR-11: 送信先を判定（/complete と同一のゲート）。
            var sensitivity = SensitivityClasses.Parse(req.Confidentiality);
            var purpose = string.IsNullOrWhiteSpace(req.Purpose) ? "default" : req.Purpose!;
            var decision = router.Route(new RoutingRequest(sensitivity, purpose, req.Model));

            if (!decision.Allowed)
            {
                // egress 拒否: プロバイダ未呼出（越境保証保持）。理由のみ返す。
                // IADR-0110: 非ストリーミングと同じ属性で計上する（経路によって観測が欠けないようにする）。
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultEgressDenied, null, decision, purpose, sensitivity);
                await Send(new CompletionStreamEvent(
                    string.Empty, Done: true, Sent: false, Text: decision.Reason, RoutingReason: decision.Reason));
                return;
            }

            var provider = services.GetKeyedService<ILlmProvider>(decision.Provider);
            if (provider is null)
            {
                logger.LogError("Provider not registered: {Provider} (endpoint {Endpoint})",
                    decision.Provider, decision.EndpointName);
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultProviderMissing, null, decision, purpose, sensitivity);
                await Send(new CompletionStreamEvent(
                    string.Empty, Done: true, Sent: false,
                    Text: $"呼び出し先プロバイダ {decision.Provider} が未登録のため送信できません。",
                    RoutingReason: decision.Reason));
                return;
            }

            // ADR-0038 決定 3 (#863): **ストリーム経路はフォールバックを実装していない**（IADR-0225 の射程外）。
            // ［2026-08-21 / #440・planning#426 裁定 (a)］従前ここには「鎖を持つのは analysis だけで、
            // ストリーム経路の用途 rag-answer は第 2 候補が計画 ADR-0038 §未決事項で未確定」と書いていた。
            // **どちらも現状と合わない** —— 鎖は analysis / diagram-coding / default / rag-answer の 4 用途が
            // 持ち、rag-answer の第 2 候補は裁定で claude-haiku-4-5 に確定した。
            // **したがって鎖を持つ用途がストリーム経路へ来ることは現に起きる。**
            // それでも実装を広げないのは、ストリームのフォールバックが「途中まで流した本文の扱い」という
            // 別の決定を要するためである（IADR-0225 が射程外と明示した理由）。下の warn が唯一の可観測点で
            // あり、**無音の穴にしないためにここで残す**。射程を広げるなら新しい ADR / IADR が要る。
            if (decision.Fallbacks.Count > 0)
                logger.LogWarning(
                    "Fallback chain {FallbackModels} is configured for this route but /complete/stream does not "
                    + "implement fallback (ADR-0038 決定 3 / IADR-0225 の射程外). endpoint={Endpoint} model={Model}",
                    string.Join(",", decision.Fallbacks), decision.EndpointName, decision.Model);

            var inputTokens = 0;
            var outputTokens = 0;
            string? stopReason = null;
            // IADR-0212 決定 3: Done を受け取れないまま終わった送信は「0 トークン」ではない。
            // 記録するのは最終チャンクで実数を受け取れたときだけである（0 埋めをしない）。
            var sawDone = false;
            var faulted = false;
            try
            {
                await foreach (var chunk in provider.StreamAsync(
                    new CompletionRequest(req.Prompt, req.MaxTokens, decision.Model), ct))
                {
                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                        await Send(new CompletionStreamEvent(chunk.TextDelta));
                    if (chunk.Done)
                    {
                        inputTokens = chunk.InputTokens;
                        outputTokens = chunk.OutputTokens;
                        stopReason = chunk.StopReason;
                        sawDone = true;
                    }
                }
                LogStopReason(logger, stopReason, decision);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 呼び出し先不調でも 500 を伝播させず、SSE で縮退イベントを返す。
                logger.LogError(ex, "LLM stream failed at endpoint {Endpoint} ({Model})",
                    decision.EndpointName, decision.Model);
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultUpstreamError, null, decision, purpose, sensitivity);
                faulted = true;
                await Send(new CompletionStreamEvent(
                    string.Empty, Done: true, Sent: false,
                    Text: $"呼び出し先 {decision.EndpointName} が現在利用できません。",
                    Model: decision.Model ?? string.Empty, RoutingReason: decision.Reason));
            }

            if (!faulted)
            {
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultSent, stopReason, decision, purpose, sensitivity,
                    sawDone ? outputTokens : null);
                // FR-10, ADR-0044 決定 1・3 (#443): 利用実績は **Done で実数を受け取れたときだけ**計上する。
                // 途中で終わった送信を 0 トークンとして積むと、費用が実態より安く見える。
                if (sawDone)
                    usage.RecordUsage(decision, purpose, sensitivity, inputTokens, outputTokens);
                await Send(new CompletionStreamEvent(
                    string.Empty, Done: true, Sent: true, Model: decision.Model ?? string.Empty,
                    InputTokens: inputTokens, OutputTokens: outputTokens, RoutingReason: decision.Reason,
                    StopReason: stopReason));
            }
        }).WithName("CompleteStream");

        return app;
    }

    // IADR-0104 (#379), ADR-0025: 送信が成立したうえでの縮退を、空応答と区別できる形で監査ログに残す。
    // refusal（安全性分類器による拒否）と max_tokens（上限到達）はいずれも本文が空になり得るが、
    // 原因も対処も異なるため別々に記録する。正常終了は従来どおり無出力（ログ量を増やさない）。
    // 拒否された本文の断片はログにも残さない（分類器が止めた内容を監査ログへ写さない）。
    private static void LogStopReason(ILogger logger, string? stopReason, RoutingDecision decision)
    {
        if (CompletionStopReasons.IsRefusal(stopReason))
            logger.LogWarning(
                "LLM refused the request (stop_reason=refusal) at endpoint {Endpoint} ({Model}). " +
                "Response body is intentionally empty; callers must not treat this as an empty completion.",
                decision.EndpointName, decision.Model);
        else if (CompletionStopReasons.IsMaxTokens(stopReason))
            logger.LogWarning(
                "LLM hit the output limit (stop_reason=max_tokens) at endpoint {Endpoint} ({Model}). " +
                "Body may be truncated or empty when extended thinking consumed the budget.",
                decision.EndpointName, decision.Model);
    }
}
