namespace GraphService.Domain.Ports;

// FR-17, ADR-0035 決定 1, [[IADR-0521]] 決定 7 (#1396): 共有タグの辺の差分を、**タグ単位で直列化する排他**。
//
// 🔴 **省いてはならない。** 共有タグの差分は「タグの所属・件数を読み → 辺を足し引きする」読み書きであり、
// `DocumentUpdated` / `DocumentDeleted` の受け口は Wolverine の既定の並列度で同時に走る（ローリング更新の
// maxSurge では 2 pod も同時に生きる）。排他が無いと、同じタグに同時に入った 2 文書が互いの未確定の行を
// 見落として組の辺が欠け、上限ちょうどのタグに同時に入った 2 文書がどちらも「ハブでない」と見て
// 上限を超えた辺を残し、削除と同時に入った文書が消えた文書への辺を残す —— **どれも次の受信まで自然回復しない。**
//
// 排他は**呼び出し元のトランザクションの寿命**で持つ（確定・取り消しで解ける）。呼び出し元は保存と同じ
// トランザクションの中で、**所属・件数を読む前に**取る。
public interface ITagEdgeLocks
{
    // 文書 1 件の排他（同じ文書への同時の受信で、タグの複製の新旧を読み違えないため）。
    // 🔴 **タグより先に、1 通につき 1 回だけ取る**（取る順序を全通で揃え、互いに待ち合わない）。
    Task LockDocumentAsync(Guid documentId, CancellationToken ct);

    // 正規化済みのタグの排他を、**渡された順に**取る。呼び出し元は序数順に並べて渡す（全通で順序を揃える）。
    Task LockTagsAsync(IReadOnlyList<string> orderedTags, CancellationToken ct);
}
