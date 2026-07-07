namespace IngestionService.Worker.Services;

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
public record EmbeddingResult(float[] Vector, string Collection, bool Embedded);
