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
    // ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0381]] (#1254): **原本が本文を持っていたか。**
    // `false` は「本文なしで完了した」（テキスト層を持たない PDF）であり、`MarkdownUri` は空の
    // `document.md` を指す。
    //
    // 🔴 **読み手はカタログ（`DocumentNormalizedConsumer`）である。** 台帳（`Document.HasBody`）へ
    // 保持し、`DocumentUpdated` へ写し、SC-03 の「本文なし（原本を参照）」の材料にする。
    // **索引側（IngestionService）はこの値ではなくチャンク 0 件で判定する**（[[IADR-0358]] 決定 1。
    // 上流の状態名に依存すると、改名や別経路で静かに漏れる）—— ただし**両者が食い違ったら警告を残す**
    // （片方だけ変わって黙って割れる形を検知する。[[IADR-0381]] 決定 3）。
    //
    // ［2026-09-05 / #1254］**否定形 `BodyAbsent`（既定 false）から改名し、極性を反転した。**
    // 同じ概念が変換側 `bodyAbsent` と検索側 `hasBody` に割れていたため、肯定形へ寄せた。
    // 末尾に既定値つきで足す（IADR-0122 決定 2）。**既定は `true`（本文あり）** ——
    // 旧発行元からのメッセージは従来と同じ「本文あり」として読める。
    bool HasBody = true,
    // FR-02, FR-03, ADR-0070 決定 4 / [[IADR-0381]] 決定 4 (#1253): **原本の所在**
    // （`RawDocumentFetched.OriginalPath`）と**データソースの表示名**。
    //
    // 🔴 **従前はここで落ちていた。** 変換側は `Path.GetFileNameWithoutExtension(OriginalPath)` で
    // 題名へ畳んだ時点でパスを捨てており、`ADR-0070` 決定 4 が要求する「タイトル・**パス**・
    // **データソース**……のメタデータで検索に載せる」のうち**題名しか下流へ届いていなかった**
    // （[[IADR-0358]] 決定 2 が記録したフォローアップ 1）。
    //
    // 末尾に既定値つきで足す（[[IADR-0122]] 決定 2。旧発行元からのメッセージは null として読める）。
    string? OriginalPath = null,
    string? DataSourceName = null);
