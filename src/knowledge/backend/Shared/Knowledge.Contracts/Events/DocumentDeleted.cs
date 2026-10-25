namespace Knowledge.Contracts.Events;

// FR-06, UC-03, Issue #88: 文書の物理削除を下流（Wiki.js 同期・検索等）へ伝播するイベント。
// Wiki.js が実コンテンツの実体を保持するため（IADR-0021）、削除の未伝播は社内文書の外部システム
// 残存リスクとなる。受信側は実体撤去（pages.delete）と同期メタデータの削除を冪等に行う。
// FR-14, IADR-0059/0062: MassTransit の URN は本名前空間（Knowledge.Contracts.Events）から導出する
// （後方互換は持たせない＝旧 URN 固定は撤廃）。
public record DocumentDeleted(
    Guid DocumentId,
    DateTimeOffset DeletedAt,
    // FR-02, FR-06, IADR-0637 (#2150): 削除した文書の**最後の発行の連番 + 1**（`DocumentUpdated.Sequence` と同じ列）。
    // 受け手（Wiki）は削除と更新を同じ鍵で比べる。**末尾・既定値付きで足す**（[[IADR-0122]] 決定 2）。`null` は旧形式。
    long? Sequence = null);
