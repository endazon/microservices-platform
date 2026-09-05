using System.Diagnostics;
using System.Runtime.CompilerServices;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Features.Completions;

// FR-04, FR-11, NFR-02, ADR-0010, ADR-0025, ADR-0029, ADR-0038, ADR-0044, ADR-0075, ADR-0076,
// IADR-0037, IADR-0101, IADR-0104, IADR-0110, IADR-0111, IADR-0212, IADR-0225, IADR-0374, IADR-0378,
// IADR-0379, IADR-0397, IADR-0398 (#1255): テキスト生成の**判定器本体**。
// REST（Complete/Endpoint・CompleteStream/Endpoint）と gRPC（GrpcService）の**両方がここを呼ぶ**。
//
// 🔴 **判定器を 2 つにしない**（IADR-0398 決定 2。#1290 が EmbedUseCase を括り出したのと同型）。
// 越境判定（router.Route）・プロバイダ解決・フォールバック鎖・計器の計上・LogStopReason は
// すべてこの中に閉じ、輸送（HTTP / gRPC）は写像だけを持つ。ここを分けると、
// **どちらか一方だけが機密区分の越境判定を通る**という最悪の食い違いが起こり得る。
//
// 🔴 **縮退は例外にしない。** 越境拒否・プロバイダ未登録・上流不調はすべて `Sent=false` の**応答**
// （一括）または `done=true, Sent=false` の**イベント**（逐次）で返す。REST が 500 を伝播させないのと
// 同じであり、gRPC 面でも RpcException にはしない（IADR-0398 決定 5）。
//
// 🔴 `isSynthetic` は**引数で受ける**。判定そのものは SyntheticTraffic.IsSyntheticInternalRequest が
// 単一情報源であり（REST は http.Request、gRPC は context.GetHttpContext().Request から呼ぶ）、
// ここで 2 つ目の定義を作らない（IADR-0378）。
public sealed class CompletionUseCase(
    ILlmRouter router,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    LlmCompletionMetrics metrics,
    LlmUsageMetrics usage)
{
    public const string CompleteLoggerCategory = "LlmGateway.Complete";
    public const string StreamLoggerCategory = "LlmGateway.CompleteStream";

    // FR-04, FR-11, ADR-0010: 一括生成（REST `POST /complete` の本体）。
    public async Task<CompletionApiResponse> ExecuteAsync(
        CompletionApiRequest req, bool isSynthetic, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(CompleteLoggerCategory);

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
            return new CompletionApiResponse(
                Text: decision.Reason, Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                Sent: false, Endpoint: null, RoutingReason: decision.Reason);
        }

        var provider = services.GetKeyedService<ILlmProvider>(decision.Provider);
        if (provider is null)
        {
            logger.LogError("Provider not registered: {Provider} (endpoint {Endpoint})",
                decision.Provider, decision.EndpointName);
            metrics.RecordCompletion(
                LlmCompletionMetrics.ResultProviderMissing, null, decision, purpose, sensitivity);
            return new CompletionApiResponse(
                Text: $"呼び出し先プロバイダ {decision.Provider} が未登録のため送信できません。",
                Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                Sent: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason);
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
                return new CompletionApiResponse(
                    result.Text, attempt.Model ?? string.Empty, result.InputTokens, result.OutputTokens,
                    Sent: true, Endpoint: attempt.EndpointName, RoutingReason: attempt.Reason,
                    StopReason: result.StopReason);
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
                // IADR-0374 (#1091): **429 が来るのはここである。** 両方（ログ・メトリクス）へ残す。
                logger.LogError(ex, "LLM call failed at endpoint {Endpoint} ({Model}) (upstream status {Status})",
                    attempt.EndpointName, attempt.Model, LlmFallbackPolicy.StatusCodeOf(ex));
                metrics.RecordCompletion(
                    LlmCompletionMetrics.ResultUpstreamError, null, attempt, purpose, sensitivity,
                    failure: ex);
                return new CompletionApiResponse(
                    Text: $"呼び出し先 {attempt.EndpointName} が現在利用できません。",
                    Model: attempt.Model ?? string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: attempt.EndpointName, RoutingReason: attempt.Reason);
            }
        }

        // 到達しない（ループは必ず return する）。コンパイラの制御フロー解析のための保険。
        throw new UnreachableException("completion attempt chain ended without a response");
    }

    // IADR-0037, FR-04, FR-11: 逐次生成（REST `POST /complete/stream` の本体）。
    //
    // 🔴 **1 チャンク 1 イベントで yield する。まとめてから返さない。**
    // 呼び出し側（REST は SSE の 1 行へ書いて flush、gRPC は IServerStreamWriter.WriteAsync）が
    // 受け取った順に送出することで、初回トークンの境界が保たれる（IADR-0398 決定 1）。
    // ここでバッファリングすると、輸送を server-streaming にした意味が消える。
    public async IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
        CompletionApiRequest req, bool isSynthetic, [EnumeratorCancellation] CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(StreamLoggerCategory);

        // FR-11: 送信先を判定（ExecuteAsync と同一のゲート）。
        var sensitivity = SensitivityClasses.Parse(req.Confidentiality);
        var purpose = string.IsNullOrWhiteSpace(req.Purpose) ? "default" : req.Purpose!;
        var decision = router.Route(new RoutingRequest(sensitivity, purpose, req.Model));

        if (!decision.Allowed)
        {
            // egress 拒否: プロバイダ未呼出（越境保証保持）。理由のみ返す。
            // IADR-0110: 非ストリーミングと同じ属性で計上する（経路によって観測が欠けないようにする）。
            metrics.RecordCompletion(
                LlmCompletionMetrics.ResultEgressDenied, null, decision, purpose, sensitivity);
            yield return new CompletionStreamEvent(
                string.Empty, Done: true, Sent: false, Text: decision.Reason, RoutingReason: decision.Reason);
            yield break;
        }

        var provider = services.GetKeyedService<ILlmProvider>(decision.Provider);
        if (provider is null)
        {
            logger.LogError("Provider not registered: {Provider} (endpoint {Endpoint})",
                decision.Provider, decision.EndpointName);
            metrics.RecordCompletion(
                LlmCompletionMetrics.ResultProviderMissing, null, decision, purpose, sensitivity);
            yield return new CompletionStreamEvent(
                string.Empty, Done: true, Sent: false,
                Text: $"呼び出し先プロバイダ {decision.Provider} が未登録のため送信できません。",
                RoutingReason: decision.Reason);
            yield break;
        }

        // ADR-0038 決定 3 (#863): **ストリーム経路はフォールバックを実装していない**（IADR-0225 の射程外）。
        // ［2026-08-21 / #440・planning#426 裁定 (a)］鎖は analysis / diagram-coding / default / rag-answer の
        // 4 用途が持ち、rag-answer の第 2 候補は裁定で claude-haiku-4-5 に確定した。
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

        // 🔴 反復子は yield を跨ぐ try/catch を持てない。列挙子を手で回し、`MoveNextAsync` だけを
        // try/catch で囲む（yield は try/finally の中にあってよい）。こうすると
        // **chunk が届いた瞬間に yield できる**（元の `await foreach` ＋ Send と同じ位置で送出する）。
        var enumerator = provider
            .StreamAsync(new CompletionRequest(req.Prompt, req.MaxTokens, decision.Model), ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                CompletionChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    chunk = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 呼び出し先不調でも 500 を伝播させず、縮退イベントを返す。
                    // IADR-0374 (#1091): ストリーム経路は鎖を持たないため**全ての上流失敗がここへ来る**
                    // （429 を含む）。非ストリームと同じ軸・同じ構造化フィールドで残す ——
                    // 経路によって観測が欠けると、レート制限の有無が「どちらの経路を使ったか」に依存する。
                    logger.LogError(ex, "LLM stream failed at endpoint {Endpoint} ({Model}) (upstream status {Status})",
                        decision.EndpointName, decision.Model, LlmFallbackPolicy.StatusCodeOf(ex));
                    metrics.RecordCompletion(
                        LlmCompletionMetrics.ResultUpstreamError, null, decision, purpose, sensitivity,
                        failure: ex);
                    faulted = true;
                    break;
                }

                if (!string.IsNullOrEmpty(chunk.TextDelta))
                    yield return new CompletionStreamEvent(chunk.TextDelta);
                if (chunk.Done)
                {
                    inputTokens = chunk.InputTokens;
                    outputTokens = chunk.OutputTokens;
                    stopReason = chunk.StopReason;
                    sawDone = true;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (faulted)
        {
            yield return new CompletionStreamEvent(
                string.Empty, Done: true, Sent: false,
                Text: $"呼び出し先 {decision.EndpointName} が現在利用できません。",
                Model: decision.Model ?? string.Empty, RoutingReason: decision.Reason);
            yield break;
        }

        CompletionEndpoints.LogStopReason(logger, stopReason, decision);
        metrics.RecordCompletion(
            LlmCompletionMetrics.ResultSent, stopReason, decision, purpose, sensitivity,
            sawDone ? outputTokens : null);
        // FR-10, ADR-0044 決定 1・3 (#443): 利用実績は **Done で実数を受け取れたときだけ**計上する。
        // 途中で終わった送信を 0 トークンとして積むと、費用が実態より安く見える。
        //
        // NFR-02, ADR-0076 決定 4, [[IADR-0378]] (#1203): 🔴 **合成監視は費用へ計上しない。**
        // 除外は黙って落とさず件数を積む（一括経路と同じ規則）。
        // **`sawDone` の条件はそのまま**である —— 途中で終わった送信は費用でも除外でもなく、
        // 「実数を受け取れなかった」であって、どちらの数にも入れない。
        if (sawDone)
        {
            if (isSynthetic)
                usage.RecordSyntheticExclusion(decision, purpose, sensitivity);
            else
                usage.RecordUsage(decision, purpose, sensitivity, inputTokens, outputTokens);
        }

        yield return new CompletionStreamEvent(
            string.Empty, Done: true, Sent: true, Model: decision.Model ?? string.Empty,
            InputTokens: inputTokens, OutputTokens: outputTokens, RoutingReason: decision.Reason,
            StopReason: stopReason);
    }
}
