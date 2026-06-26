namespace LlmGateway.Api.Providers;

// ADR-0010: LLM 抽象化レイヤー — P1以降で実装を差し替え可能にする
public interface ILlmProvider
{
    Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public record CompletionRequest(string Prompt, int MaxTokens = 1024, string? Model = null);
public record CompletionResult(string Text, int InputTokens, int OutputTokens);
