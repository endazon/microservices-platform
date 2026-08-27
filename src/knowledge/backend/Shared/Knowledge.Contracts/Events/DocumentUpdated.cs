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
    string? ContentFingerprint = null);
