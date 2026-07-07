namespace LlmGateway.Api.Providers;

// FR-02, FR-03, ADR-0013, ADR-0016, ADR-0017: 埋め込み生成ポート。
// LLM 生成（ILlmProvider）とは別系統。プロバイダ追加・差し替えの影響をアダプタに閉じる（ADR-0013）。
// モデル・次元は呼び出し側（ルーターの決定）から渡し、アダプタは送信先の HTTP 契約のみを担う。
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, string model, int dimensions, CancellationToken ct = default);
}
