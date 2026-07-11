using MassTransit;

namespace Knowledge.Contracts.Events;

// FR-02, UC-04: 取り込みサービスへの処理依頼イベント
// FR-14, IADR-0059: knowledge ユニット固有の契約。MessageUrn を旧名前空間に固定し wire 後方互換を維持する。
[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:IngestionRequested")]
public record IngestionRequested(
    Guid DocumentId,
    Guid JobId,
    DateTimeOffset RequestedAt);
