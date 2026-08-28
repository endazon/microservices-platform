using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, IADR-0109 (#394): OpenAI 互換 API の `choices[].finish_reason` を、応答契約の正準語彙
// （CompletionStopReasons。Anthropic 由来）へ正規化する。
//
// ゲートウェイは「呼び出し先の差を吸収して統一契約で返す」ために存在する（ADR-0010）。語彙の差の吸収も
// その責務であり、素通しして呼び出し側（RagOrchestrator / LlmGatewayDiagramCoder / AST 取引判断・報告書生成）
// に両方の語彙を覚えさせない。1 箇所でも追随漏れがあると `content_filter` が拒否として扱われず、
// IADR-0104 の fail-safe（拒否された断片を下流の判断材料にしない）が破れる。
//
// OpenAI 固有の語彙は共有契約（Platform.Shared.Contracts）へ持ち込まない。finish_reason は
// トランスポートの関心事であり、契約の関心事ではない（IADR-0109 §決定 1）。
internal static class OpenAiFinishReasons
{
    // OpenAI 互換の語彙。function_call は OpenAI で非推奨だが、互換実装が返し得るため写像に含める。
    private const string Stop = "stop";
    private const string Length = "length";
    private const string ContentFilter = "content_filter";
    private const string ToolCalls = "tool_calls";
    private const string FunctionCall = "function_call";

    private static readonly Dictionary<string, string> ToCanonical = new(StringComparer.OrdinalIgnoreCase)
    {
        [Stop] = CompletionStopReasons.EndTurn,
        [Length] = CompletionStopReasons.MaxTokens,
        [ContentFilter] = CompletionStopReasons.Refusal,
        [ToolCalls] = CompletionStopReasons.ToolUse,
        [FunctionCall] = CompletionStopReasons.ToolUse,
    };

    // finish_reason を正準語彙へ写像する。
    //   StopReason — 写像結果。未知値は**原文のまま**返す（既定値へ潰すと拒否・打ち切りが
    //                「正常終了」に見えてしまう。IADR-0104 が文字列型を選んだ理由と同じ）。
    //   Unmapped   — 写像表に無い値だった（呼び出し側が warn ログへ残す）。欠落・null・空白は
    //                「未対応プロバイダと同じ null」であり未知語彙ではないため false（正常系のログを汚さない）。
    public static (string? StopReason, bool Unmapped) Map(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
            return (null, false);

        return ToCanonical.TryGetValue(finishReason, out var canonical)
            ? (canonical, false)
            : (finishReason, true);
    }
}
