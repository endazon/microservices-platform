namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-02, UC-04: 取り込みサービスが索引登録完了時に発行するイベント
public record IngestionCompleted(
    Guid DocumentId,
    int ChunkCount,
    DateTimeOffset CompletedAt);
