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
    DateTimeOffset FetchedAt,
    // FR-02, FR-03, ADR-0070 決定 4 / [[IADR-0388]] 決定 4 (#1253): **データソースの表示名**
    // （`DataSource.Name`）。本文を持たない文書を「データソース名」で検索に載せるために運ぶ。
    // 正本は `SourceId` であり**これは表示名の複写である**（[[IADR-0153]] 決定 1 の例外ではない
    // ——射影＝索引テキストは人が読む面であり、改名の追随義務は無い。次の同期で上書きされる）。
    // 末尾に既定値つきで足す（[[IADR-0122]] 決定 2。旧発行元からのメッセージは null として読める）。
    string? SourceName = null);
