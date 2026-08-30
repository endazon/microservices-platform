using Platform.Shared.Contracts.Dtos;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Completions.Complete;
using LlmGateway.Features.Completions.CompleteStream;

namespace LlmGateway.Features.Completions;

// FR-04, FR-11, ADR-0010: テキスト生成スライスの合成点（/complete・/complete/stream）。
//
// ADR-0065 決定 2: 1 ユースケースのファイルは操作フォルダ（Complete/ ・CompleteStream/）に束ねる。
// **本ファイルに残すのは、両操作が共有するもの（グループの構築と LogStopReason）だけである。**
public static class CompletionEndpoints
{
    public static IEndpointRouteBuilder MapCompletionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Completions");

        g.MapComplete();
        g.MapCompleteStream();

        return app;
    }

    // IADR-0104 (#379), ADR-0025: 送信が成立したうえでの縮退を、空応答と区別できる形で監査ログに残す。
    // refusal（安全性分類器による拒否）と max_tokens（上限到達）はいずれも本文が空になり得るが、
    // 原因も対処も異なるため別々に記録する。正常終了は従来どおり無出力（ログ量を増やさない）。
    // 拒否された本文の断片はログにも残さない（分類器が止めた内容を監査ログへ写さない）。
    //
    // **非ストリーミングとストリーミングの両操作が呼ぶ**ため集約直下に残す（ADR-0065 決定 2）。
    internal static void LogStopReason(ILogger logger, string? stopReason, RoutingDecision decision)
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
