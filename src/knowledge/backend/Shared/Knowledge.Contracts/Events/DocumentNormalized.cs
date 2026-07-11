namespace Knowledge.Contracts.Events;

// FR-12, UC-06: 変換サービスが正規化完了時に発行するイベント
// FR-14, IADR-0059/0062: knowledge ユニット固有の契約。MassTransit の URN は本名前空間
// （Knowledge.Contracts.Events）から導出する（後方互換は持たせない＝旧 URN 固定は撤廃）。
public record DocumentNormalized(
    Guid DocumentId,
    Guid SourceId,
    string Title,
    string MarkdownUri,
    List<string> AssetUris,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset NormalizedAt);
