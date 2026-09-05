namespace Knowledge.Contracts.Events;

// FR-06, UC-03: 文書管理サービスが発行するイベント（登録・更新時）
// FR-14, IADR-0059/0062: knowledge ユニット固有の契約。MassTransit の URN は本名前空間
// （Knowledge.Contracts.Events）から導出する（後方互換は持たせない＝旧 URN 固定は撤廃）。
// ADR-0050 決定 1 (#911): 本文指紋（ContentFingerprint）を運ぶ。**本文の内容のみに依存する
// 不透明な値**であり、契約が要求する性質は「本文が変われば変わり、変わらなければ変わらない」だけ
// （算出方法は発行側の実装詳細。現行は正規化 Markdown の UTF-8 バイト列の SHA-256 小文字 hex）。
// 却下済み AI 提案の解除判定（ADR-0033 決定 10）と再取り込みの要否判定（ADR-0050 決定 3）に使う。
// **末尾・既定値付きで足す**（途中挿入は位置引数を壊す。IADR-0122 決定 2 の非破壊条件）。
// null は「発行側が本文を指紋化できなかった」（本文なし・ストレージ縮退）を表す。
public record DocumentUpdated(
    Guid DocumentId,
    string Title,
    string Status,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset UpdatedAt,
    string? ContentFingerprint = null,
    // ADR-0070 決定 3・決定 4 / [[IADR-0388]] (#1254 / #1253): **原本が本文を持っていたか**と、
    // **原本の所在・データソースの表示名**。カタログ（`Document`）が台帳へ保持した値を写す。
    //
    // - `HasBody`: `false` は「本文なしで完了した」（テキスト層を持たない PDF）。SC-03 の
    //   「本文なし（原本を参照）」の材料であり、**索引側の判定材料ではない**
    //   （索引はチャンク 0 件で判定する。[[IADR-0358]] 決定 1）——
    //   ただし**両者が食い違ったら取り込みが警告を残す**（[[IADR-0388]] 決定 3）。
    // - `OriginalPath` / `DataSourceName`: 本文なしの文書の索引テキストへ載せる
    //   （`MetadataIndexText`。[[IADR-0388]] 決定 4）。**本文ありのチャンクには載せない**
    //   （同 決定 5 の非対称）。
    //
    // **末尾・既定値付きで足す**（[[IADR-0122]] 決定 2）。**既定は「本文あり」「所在は不明」**で、
    // 旧発行元からのメッセージは従来と同じに読める。
    bool HasBody = true,
    string? OriginalPath = null,
    string? DataSourceName = null);
