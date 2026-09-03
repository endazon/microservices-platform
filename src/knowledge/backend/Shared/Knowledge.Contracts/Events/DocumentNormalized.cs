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
    DateTimeOffset NormalizedAt,
    // ADR-0070 決定 3 / IADR-0362 (#1192): **本文なしで完了した**（テキスト層を持たない PDF）。
    // MarkdownUri は空の document.md を指す。後続（カタログ・索引。ADR-0070 決定 4 の射程）は
    // 本文由来のチャンクを作らず、メタデータで検索に載せる判断にこれを使う。
    // 末尾に既定値つきで足す（IADR-0122 決定 2。旧発行元からのメッセージは false として読める）。
    bool BodyAbsent = false);
