namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-02, UC-04: 取り込みサービスへの処理依頼イベント
public record IngestionRequested(
    Guid DocumentId,
    Guid JobId,
    DateTimeOffset RequestedAt);
