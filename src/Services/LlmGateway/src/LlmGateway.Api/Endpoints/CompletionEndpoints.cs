using LlmGateway.Api.Providers;
using LlmGateway.Api.Routing;

namespace LlmGateway.Api.Endpoints;

// FR-04, FR-11, ADR-0010: テキスト生成エンドポイント（/complete）
// FR-11: 入力の機密区分・用途に応じて呼び出し先（ティア/エンドポイント/モデル）を切り替える。
public static class CompletionEndpoints
{
    public static IEndpointRouteBuilder MapCompletionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Completions");

        // AiAnalysisService が POST /complete で呼び出す
        g.MapPost("/complete", async (
            CompletionApiRequest req,
            ILlmRouter router,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
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
                return Results.Ok(new CompletionApiResponse(
                    Text: decision.Reason, Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: null, RoutingReason: decision.Reason));
            }

            var provider = services.GetKeyedService<ILlmProvider>(decision.Provider);
            if (provider is null)
            {
                logger.LogError("Provider not registered: {Provider} (endpoint {Endpoint})",
                    decision.Provider, decision.EndpointName);
                return Results.Ok(new CompletionApiResponse(
                    Text: $"呼び出し先プロバイダ {decision.Provider} が未登録のため送信できません。",
                    Model: string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }

            try
            {
                var result = await provider.CompleteAsync(
                    new CompletionRequest(req.Prompt, req.MaxTokens, decision.Model), ct);
                return Results.Ok(new CompletionApiResponse(
                    result.Text, decision.Model ?? string.Empty, result.InputTokens, result.OutputTokens,
                    Sent: true, Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 呼び出し先が不調な場合も 500 を伝播させず、縮退可能な応答を返す。
                logger.LogError(ex, "LLM call failed at endpoint {Endpoint} ({Model})",
                    decision.EndpointName, decision.Model);
                return Results.Ok(new CompletionApiResponse(
                    Text: $"呼び出し先 {decision.EndpointName} が現在利用できません。",
                    Model: decision.Model ?? string.Empty, InputTokens: 0, OutputTokens: 0,
                    Sent: false, Endpoint: decision.EndpointName, RoutingReason: decision.Reason));
            }
        }).WithName("Complete").Produces<CompletionApiResponse>();

        return app;
    }
}

// FR-11: confidentiality（入力文書の最高機密区分）・purpose（用途）で呼び出し先を切り替える。
public record CompletionApiRequest(
    string Prompt,
    int MaxTokens = 1024,
    string? Model = null,
    string? Confidentiality = null,
    string? Purpose = null);

public record CompletionApiResponse(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    bool Sent,
    string? Endpoint,
    string? RoutingReason);
