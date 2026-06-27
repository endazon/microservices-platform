namespace IngestionService.Worker.Services;

// FR-02, ADR-0009: IngestionService が Qdrant へ書き込む際のポート
public interface IIngestionVectorStore
{
    // FR-02: 索引（コレクション）の存在を保証する（起動時ブートストラップ）
    Task EnsureCollectionAsync(CancellationToken ct = default);

    Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        CancellationToken ct = default);

    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default);
}
