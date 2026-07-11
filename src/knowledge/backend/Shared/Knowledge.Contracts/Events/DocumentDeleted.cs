namespace Knowledge.Contracts.Events;

// FR-06, UC-03, Issue #88: 文書の物理削除を下流（Wiki.js 同期・検索等）へ伝播するイベント。
// Wiki.js が実コンテンツの実体を保持するため（IADR-0021）、削除の未伝播は社内文書の外部システム
// 残存リスクとなる。受信側は実体撤去（pages.delete）と同期メタデータの削除を冪等に行う。
// FR-14, IADR-0059/0062: MassTransit の URN は本名前空間（Knowledge.Contracts.Events）から導出する
// （後方互換は持たせない＝旧 URN 固定は撤廃）。
public record DocumentDeleted(
    Guid DocumentId,
    DateTimeOffset DeletedAt);
