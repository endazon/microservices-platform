namespace Knowledge.Contracts.Events;

// FR-02, UC-04: 取り込みサービスへの処理依頼イベント
// FR-14, IADR-0059/0062: knowledge ユニット固有の契約。MassTransit の URN は本名前空間
// （Knowledge.Contracts.Events）から導出する（後方互換は持たせない＝旧 URN 固定は撤廃）。
public record IngestionRequested(
    Guid DocumentId,
    Guid JobId,
    DateTimeOffset RequestedAt);
