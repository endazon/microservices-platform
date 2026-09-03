namespace GraphService.Domain.Ports;

// FR-18, SC-03, ADR-0063 決定 1〜3, IADR-0361 決定 1 (#1187): **タグ提案の承認を文書のタグへ反映する**ポート。
//
// 反映先は DocumentService（本サービスは文書のタグを持たない。ADR-0033 決定 2 の複製は ABAC 属性と
// 表題だけである）。🔴 **承認者本人の資格で書く** —— 実装は要求の `Authorization` をそのまま転送し、
// サービスアカウントを持たない（決定 3「サービスが利用者に代わって書く形は採らない」）。
//
// 戻り値は 4 値で、**例外を投げない**。呼び出し側（承認の口）が状態コードへ写す:
//   Applied     → 承認を確定する（200）
//   UnknownTag  → 辞書に無い。承認できず却下のみ（400 `unknown_tag`。決定 2 後段）
//   NotWritable → 後段の最終防衛線が拒んだ（404。存在秘匿の一本道）
//   Unavailable → 後段へ到達できない／後段の失敗（502。**成功へ縮退しない**）
public interface IDocumentTagWriter
{
    Task<TagWriteOutcome> AddTagAsync(Guid documentId, string tagName, CancellationToken ct = default);
}

public enum TagWriteOutcome
{
    Applied,
    UnknownTag,
    NotWritable,
    Unavailable,
}
