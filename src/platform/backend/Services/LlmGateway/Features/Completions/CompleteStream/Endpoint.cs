using System.Text.Encodings.Web;
using System.Text.Json;
using Platform.Shared.Contracts.Dtos;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;

namespace LlmGateway.Features.Completions.CompleteStream;

// IADR-0037, FR-04, FR-11: SSE ストリーミング版のテキスト生成（POST /complete/stream）。
// FR-11: egress ゲートは /complete と同一の router.Route(...) を通し、Allowed=false は
// プロバイダを一切呼ばず理由イベントのみ返す（越境保証を弱めない）。
public static class CompleteStreamEndpoint
{
    // SSE の data: 行に載せる JSON は Web 既定（camelCase）で直列化し、呼び出し側の JSON と揃える。
    // 日本語を \uXXXX へ過剰エスケープしないよう緩和エンコーダを用いる（SSE 本文の可読性・帯域）。
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapCompleteStream(this IEndpointRouteBuilder app)
    {
        // AiAnalysisService が POST /complete/stream で呼び出す。
        app.MapPost("/complete/stream", async (
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
            // 前段プロキシでの応答バッファリング抑止。#1135 で SPA の前段が nginx から Caddy へ移ったが、
            // **Caddy の reverse_proxy も同じヘッダを解釈する**ため値は変えない（デファクト標準のヘッダ）。
            http.Response.Headers["X-Accel-Buffering"] = "no";

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
                CompletionEndpoints.LogStopReason(logger, stopReason, decision);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 呼び出し先不調でも 500 を伝播させず、SSE で縮退イベントを返す。
                // IADR-0374 (#1091): ストリーム経路は鎖を持たないため**全ての上流失敗がここへ来る**
                // （429 を含む）。非ストリームと同じ軸・同じ構造化フィールドで残す ——
                // 経路によって観測が欠けると、レート制限の有無が「どちらの経路を使ったか」に依存する。
                logger.LogError(ex, "LLM stream failed at endpoint {Endpoint} ({Model}) (upstream status {Status})",
                    decision.EndpointName, decision.Model, LlmFallbackPolicy.StatusCodeOf(ex));
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultUpstreamError, null, decision, purpose, sensitivity,
                    failure: ex);
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
}
