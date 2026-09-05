namespace DocumentService.Domain.Ports;

// FR-06, UC-03 / ADR-0027（E3b）: 文書更新イベントの発行口。
//
// 🔴 **抽象を挟む理由は IDocumentDeletedPublisher と同じ**（トポロジ検査の導出単位がファイル）。
// 加えて E3b では、**発行 8 箇所のうち 7 箇所が検査器に不可視**（`Publish(ToEvent(...))` の形。
// IADR-0245 決定 8 の実測）だった。ポートへ集約しアダプタ側でイベントを構築することで、
// **可視の発行点が 1 箇所に定まり**、DocumentUpdated の発行トランスポートが表に正しく載る。
//
// ⚠️ 引数は素の値で渡す（契約型を渡すと `findPublishers` から発行が見えなくなる）。
// 識別子 → 表示名の変換（タグ）は呼び出し側の責務のまま（変換点は
// `DocumentEndpoints.PublishUpdatedAsync` の 1 つ。IADR-0153 決定 2）。
public interface IDocumentUpdatedPublisher
{
    // ⚠️ Wolverine の `IMessageBus.PublishAsync` は CancellationToken を取らない（E1 仕様書
    // 「受け入れた挙動差」）。ct は契約として受けるが、現行実装では伝播されない。
    // contentFingerprint: ADR-0050 決定 1 (#911)。本文の内容のみに依存する不透明な値（null = 不明）。
    Task PublishUpdatedAsync(
        Guid documentId,
        string title,
        string status,
        string? markdownUri,
        Dictionary<string, string> attributes,
        List<string> tags,
        DateTimeOffset updatedAt,
        string? contentFingerprint = null,
        // ADR-0070 決定 3・決定 4 / [[IADR-0388]] 決定 2・4 (#1254 / #1253):
        // 原本が本文を持っていたか（SC-03 の材料）と、原本の所在・データソースの表示名
        // （本文なしの文書の索引テキストの材料）。**台帳（`Document`）の値を写す** ——
        // 属性編集やタグ改名による再発行でも同じ値が乗る。
        bool hasBody = true,
        string? originalPath = null,
        string? dataSourceName = null,
        CancellationToken ct = default);
}
