namespace IngestionService.Domain.Ports;

// FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 1・2 (#1908): **語彙索引に全文だけで書いた、埋め込み待ちの点**（読み戻した形）。
//
// 書いたときの口（`UpsertEmbeddingPendingChunkAsync` / `UpsertEmbeddingPendingMetadataPointAsync`）の引数を、
// ペイロードからそのまま復元したものである。埋め直しの器はこれを埋め、**同じ ID・同じペイロード**で主へ書く。
// `ChunkIndex = -1`（`ChunkId.MetadataChunkIndex`）はメタデータ点（`has_body = false`。`Text` は索引テキスト）である。
public sealed record EmbeddingPendingPoint(
    Guid PointId,
    Guid DocumentId,
    string Title,
    string Text,
    int ChunkIndex,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset? UpdatedAt,
    List<string>? SharedWith,
    string Reason)
{
    public bool IsMetadataPoint => ChunkIndex == ChunkId.MetadataChunkIndex;
}

// FR-02, [[IADR-0536]] 決定 1 (#1908): 埋め込み待ちになった理由（閉じた値域。計器のタグとログに載る）。
public static class EmbeddingPendingReasons
{
    // ゲートウェイが一時障害（`Retryable=true`。鍵の未設定・送信先の不調）を返した。
    public const string Transient = "transient";

    // ゲートウェイが恒久的な拒否（`Retryable=false`。経路の無効化・次元不整合）を返した。
    public const string Rejected = "rejected";

    public static string From(EmbeddingResult result) => result.Retryable ? Transient : Rejected;
}
