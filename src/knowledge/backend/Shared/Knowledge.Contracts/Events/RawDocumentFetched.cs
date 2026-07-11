namespace Knowledge.Contracts.Events;

// FR-01, FR-02, UC-04: データソース連携サービスが発行するイベント
// FR-14, IADR-0059/0062: knowledge ユニット固有の契約。MassTransit の URN は本名前空間
// （Knowledge.Contracts.Events）から導出する（後方互換は持たせない＝旧 URN 固定は撤廃）。
public record RawDocumentFetched(
    Guid FetchId,
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string StorageUri,
    string ContentType,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset FetchedAt);
