namespace GraphService.Domain.Ports;

// FR-18, SC-09, ADR-0063 決定 2, IADR-0361 決定 2 (#1014): **タグ辞書の名前集合**を読むポート。
//
// 提案の**生成段**が「LLM に選ばせる値集合」として使い、返ってきた提案を突き合わせて
// 辞書外を落とす（辺の型辞書 `db.EdgeTypes` と同じ形。辞書の権威は DocumentService）。
//
// 🔴 **戻り値 null は「引けなかった」を表す。空集合（辞書が空）とは別である。**
// 呼び出し側は null を **fail-closed** に読む —— タグ提案を 1 件も作らない
// （「辞書外の値を持つ提案は生成しない」を、辞書が分からないときにも守る）。
public interface ITagDictionaryReader
{
    Task<IReadOnlySet<string>?> ReadNamesAsync(CancellationToken ct = default);
}
