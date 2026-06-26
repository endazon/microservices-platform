namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-02: 取り込み完了イベント（チャンク化・埋め込み・索引登録完了）
public record IngestionCompleted(
    Guid DocumentId,
    Guid JobId,
    int ChunkCount,
    DateTimeOffset CompletedAt);
