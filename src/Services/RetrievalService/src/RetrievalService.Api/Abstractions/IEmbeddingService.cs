namespace RetrievalService.Api.Abstractions;

// FR-03, ADR-0013: 埋め込み生成ポート（LLM ゲートウェイ経由）
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
