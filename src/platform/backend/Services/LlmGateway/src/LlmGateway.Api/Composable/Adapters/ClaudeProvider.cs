using System.Runtime.CompilerServices;
using LlmGateway.Api.Foundation.Ports;
using Platform.Shared.Contracts.Dtos;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;

namespace LlmGateway.Api.Composable.Adapters;

// ADR-0010: Claude SDK デフォルト実装。既定モデルは claude-opus-5（ADR-0025 追従・IADR-0101。
// 既定 Opus 経路そのものの決定は IADR-0022）。
// 定型用途は claude-sonnet-5（ADR-0022 追従・IADR-0106）/ claude-haiku-4-5、最難関用途は claude-fable-5 を
// ルーター（用途別）で選択する。
// ⚠️ Opus 5 / Sonnet 5 は thinking（拡張思考）が既定で有効であり、MaxTokens は思考トークンと本文の
// 合算上限になる。切り詰めると本文が途中で切れるため、既定値は思考分の余裕を含める（IADR-0101）。
// なお本実装は thinking / temperature / top_p / top_k / assistant prefill を送らない（Opus 5 で 400 になる
// パラメータを持ち込まないため。変更する場合は IADR-0101 の選択肢 2 の検討結果を参照）。
public class ClaudeProvider(AnthropicClient client, IConfiguration config) : ILlmProvider
{
    private readonly string _model = config["Llm:Model"] ?? "claude-opus-5";

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        var msg = await client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model = request.Model ?? _model,
            MaxTokens = request.MaxTokens,
            Messages =
            [
                new Message
                {
                    Role = RoleType.User,
                    Content = [new TextContent { Text = request.Prompt }]
                }
            ]
        }, ct);

        // IADR-0104 (#379), ADR-0025: content を読む前に stop_reason を確認する。refusal は HTTP 200・
        // 例外なしで到着するため、判別しないと空文字へ静かに縮退し「送信したが空応答」と区別できない。
        var stopReason = msg.StopReason;

        // 拒否時は本文を返さない。安全性分類器は本文の途中で停止し得るため、断片が非空のまま
        // 下流へ流れると（AST 取引判断は Text の非空を根拠に売買判断へ進む）fail-safe が破れる。
        // max_tokens の途中結果は正当な観測対象であり破棄しない（IADR-0101 / IADR-0104 §決定）。
        var text = CompletionStopReasons.IsRefusal(stopReason)
            ? string.Empty
            : msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";

        return new CompletionResult(text, msg.Usage.InputTokens, msg.Usage.OutputTokens, stopReason);
    }

    // IADR-0037: Anthropic SDK の SSE ストリーミングで本文デルタを逐次返す（真のストリーミング・主経路）。
    // 呼び出し側（LlmGateway /complete/stream）は egress ルーティングで送信可と判定した後にのみ本メソッドを呼ぶ。
    public async IAsyncEnumerable<CompletionChunk> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new MessageParameters
        {
            Model = request.Model ?? _model,
            MaxTokens = request.MaxTokens,
            Stream = true,
            Messages =
            [
                new Message
                {
                    Role = RoleType.User,
                    Content = [new TextContent { Text = request.Prompt }]
                }
            ]
        };

        int inputTokens = 0, outputTokens = 0;
        string? stopReason = null;
        await foreach (var res in client.Messages.StreamClaudeMessageAsync(parameters, ct))
        {
            // message_start で入力トークン、message_delta で出力トークンが逐次確定する。
            if (res.StreamStartMessage?.Usage is { } startUsage)
                inputTokens = startUsage.InputTokens;
            if (res.Usage is { } usage && usage.OutputTokens > 0)
                outputTokens = usage.OutputTokens;

            // IADR-0104: 終了理由は message_delta（末尾）で確定する。既に送出したデルタは撤回できないため、
            // 最終チャンクへ載せて呼び出し側が破棄判断できるようにする（トレードオフは IADR-0104 §結果）。
            if (res.Delta?.StopReason is { Length: > 0 } deltaStop)
                stopReason = deltaStop;
            else if (res.StopReason is { Length: > 0 } msgStop)
                stopReason = msgStop;

            var delta = res.Delta?.Text;
            if (!string.IsNullOrEmpty(delta))
                yield return new CompletionChunk(delta);
        }

        // 最終チャンク: 本文増分なし・トークン数と終了理由を伴う（呼び出し側が集計と縮退判断を確定できる）。
        yield return new CompletionChunk(string.Empty, Done: true, inputTokens, outputTokens, stopReason);
    }
}
