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

            try
            {
                var result = await provider.CompleteAsync(
                    new CompletionRequest(req.Prompt, req.MaxTokens, decision.Model), ct);
                LogStopReason(logger, result.StopReason, decision);
                // IADR-0110: 越境が成立した呼び出し（拒否率の分母）。終了理由は別属性で載せる。
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultSent, result.StopReason, decision, purpose, sensitivity,
                    result.OutputTokens);
                return Results.Ok(new CompletionApiResponse(
                    result.Text, decision.Model ?? string.Empty, result.InputTokens, result.OutputTokens,
                    Sent: true, Endpoint: decision.EndpointName, RoutingReason: decision.Reason,
                    StopReason: result.StopReason));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 呼び出し先が不調な場合も 500 を伝播させず、縮退可能な応答を返す。
                logger.LogError(ex, "LLM call failed at endpoint {Endpoint} ({Model})",
                    decision.EndpointName, decision.Model);
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultUpstreamError, null, decision, purpose, sensitivity);
                return Results.Ok(new CompletionApiResponse(
                    Text: $"呼び出し先 {decision.EndpointName} が現在利用できません。",
                    Model: decision.Model ?? string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }
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

            var inputTokens = 0;
            var outputTokens = 0;
            string? stopReason = null;
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
                    outputTokens);
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
