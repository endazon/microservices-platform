namespace LlmGateway.Api.Foundation.Ports;

// ADR-0010: LLM 抽象化レイヤー — P1以降で実装を差し替え可能にする。
// 埋め込みは別系統（IEmbeddingProvider / ADR-0013・ADR-0016）へ分離した。
public interface ILlmProvider
{
    Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
}

public record CompletionRequest(string Prompt, int MaxTokens = 1024, string? Model = null);
public record CompletionResult(string Text, int InputTokens, int OutputTokens);
