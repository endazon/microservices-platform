namespace IngestionService.Worker.Foundation.Ports;

// ADR-0013, ADR-0016: 埋め込み生成ポート。
// 埋め込みは機密区分により送信先・モデル・コレクション・次元が分かれるため、
// ベクトルに加えて索引対象コレクションと成否（fail-closed 判定）を返す。
public interface IEmbeddingService
{
    Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default);
}

// FR-02, ADR-0016: 埋め込み結果。
//   Embedded=false は機密区分による送信拒否（fail-closed）・次元不整合・呼び出し失敗を示し、索引をスキップする。
//   Collection は索引対象のモデル別コレクション名（例 knowledge_chunks_voyage_3_5 / _ruri_v3）。
//   Retryable=true は「一時的な障害（送信先の不調・タイムアウト等）で今回は埋め込めなかった」ことを示す。
//   このとき消費側は恒久スキップにせず、例外を送出して MassTransit のリトライ/DLQ に委ねる（取りこぼし防止）。
//   fail-closed・次元不整合など恒久的な理由は Retryable=false（=スキップ）。
public record EmbeddingResult(float[] Vector, string Collection, bool Embedded, bool Retryable = false);
