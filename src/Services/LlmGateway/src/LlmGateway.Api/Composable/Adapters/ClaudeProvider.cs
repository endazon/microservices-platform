using System.Runtime.CompilerServices;
using LlmGateway.Api.Foundation.Ports;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;

namespace LlmGateway.Api.Composable.Adapters;

// ADR-0010: Claude SDK デフォルト実装。既定モデルは claude-opus-4-8（(b) 実装追従・IADR-0022）。
// 定型用途は claude-sonnet-4-6 / claude-haiku-4-5、最難関用途は claude-fable-5 をルーター（用途別）で選択する。
public class ClaudeProvider(AnthropicClient client, IConfiguration config) : ILlmProvider
{
    private readonly string _model = config["Llm:Model"] ?? "claude-opus-4-8";

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

        var text = msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
        return new CompletionResult(text, msg.Usage.InputTokens, msg.Usage.OutputTokens);
    }

    // IADR-0036: Anthropic SDK の SSE ストリーミングで本文デルタを逐次返す（真のストリーミング・主経路）。
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
        await foreach (var res in client.Messages.StreamClaudeMessageAsync(parameters, ct))
        {
            // message_start で入力トークン、message_delta で出力トークンが逐次確定する。
            if (res.StreamStartMessage?.Usage is { } startUsage)
                inputTokens = startUsage.InputTokens;
            if (res.Usage is { } usage && usage.OutputTokens > 0)
                outputTokens = usage.OutputTokens;

            var delta = res.Delta?.Text;
            if (!string.IsNullOrEmpty(delta))
                yield return new CompletionChunk(delta);
        }

        // 最終チャンク: 本文増分なし・トークン数を伴う（呼び出し側が集計を確定できる）。
        yield return new CompletionChunk(string.Empty, Done: true, inputTokens, outputTokens);
    }
}
