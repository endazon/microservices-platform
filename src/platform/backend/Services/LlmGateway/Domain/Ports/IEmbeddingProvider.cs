using LlmGateway.Domain.Routing;

namespace LlmGateway.Domain.Ports;

// FR-02, FR-03, ADR-0013, ADR-0016, ADR-0017: 埋め込み生成ポート。
// LLM 生成（ILlmProvider）とは別系統。プロバイダ追加・差し替えの影響をアダプタに閉じる（ADR-0013）。
// モデル・次元は呼び出し側（ルーターの決定）から渡し、アダプタは送信先の HTTP 契約のみを担う。
public interface IEmbeddingProvider
{
    // #809: 用途（クエリ／索引）を渡す。**埋め込みモデルは両者を区別して符号化することを要求する。**
    // Ruri v3 は 1+3 プレフィクス（"検索クエリ: " / "検索文書: "）を必須とし、Voyage は input_type を取る。
    // 用途を渡さないと両者が同じベクトルになり、検索品質が下がる。しかも**プレフィクス必須の Ruri のほうが
    // 大きく損をする**ため、ADR-0017 の「voyage 比で劣化するなら BGE-M3 へ」という切替判断を誤らせる。
    Task<float[]> EmbedAsync(
        string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default);
}
