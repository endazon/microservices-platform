using Anthropic.SDK;
using Anthropic.SDK.Messaging;

namespace LlmGateway.Api.Providers;

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

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>()); // P1: embeddings API
}
