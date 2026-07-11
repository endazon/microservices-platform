using MassTransit;

namespace Knowledge.Contracts.Events;

// FR-02, UC-04: 取り込みサービスが索引登録完了時に発行するイベント
// FR-14, IADR-0059: knowledge ユニット固有の契約。MessageUrn を旧名前空間に固定し wire 後方互換を維持する。
[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:IngestionCompleted")]
public record IngestionCompleted(
    Guid DocumentId,
    int ChunkCount,
    DateTimeOffset CompletedAt);
