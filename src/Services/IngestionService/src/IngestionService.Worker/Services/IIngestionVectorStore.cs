namespace IngestionService.Worker.Services;

// FR-02, ADR-0009: IngestionService が Qdrant へ書き込む際のポート
public interface IIngestionVectorStore
{
    Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        CancellationToken ct = default);

    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default);
}
