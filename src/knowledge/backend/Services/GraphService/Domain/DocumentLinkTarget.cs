namespace GraphService.Domain;

// FR-10, FR-17, UC-05, SC-10, ADR-0033 決定 4, [[IADR-0389]] (#1246):
// 本文から抽出した**リンク先の名前**。文書 1 件につき、その本文が指す相手の名前の集合を持つ。
//
// ## 🔴 保存するのは「解決の失敗」ではなく「リンク先の名前」である（[[IADR-0389]] 決定 3）
//
// 素直な実装は「解決に失敗したリンクを表へ落とす」だが、**それでは指標が壊れる**。
// リンクが解決できるかは**相手の側の事情で変わる**（相手が改名された・削除された）。
// 失敗を保存すると、相手が消えて A の `[[B]]` が壊れても、**A が再取り込みされるまで
// 未解決に数えられない**。リンク切れを数える指標が、リンク切れの主因を取りこぼす。
//
// 名前を保存し、**集計のたびに解決し直す**（`KnowledgeHealthCollector`）。
// こちらは相手の改名・削除を次の収集周期で必ず拾う。
//
// 抽出結果そのものであり、**辺が作られたかどうかとは独立**である（自己参照・辞書に無い型で
// 辺が作られなかった場合も、リンク先としては解決できているので未解決ではない）。
public class DocumentLinkTarget
{
    // `graph_documents.Title` と同値（突合する相手の長さ）。
    public const int MaxTargetLength = 1000;

    public Guid Id { get; private set; } = Guid.NewGuid();

    // リンクを書いている側の文書。**この文書の再取り込みで全量置換する。**
    public Guid SourceDocumentId { get; private set; }

    // リンク先の名前（`[[名前]]` の名前部分）。**文書 ID ではない** —— 解決できるとは限らない。
    public string Target { get; private set; } = string.Empty;

    public DateTimeOffset ExtractedAt { get; private set; } = DateTimeOffset.UtcNow;

    private DocumentLinkTarget() { }

    public static DocumentLinkTarget Create(Guid sourceDocumentId, string target, DateTimeOffset extractedAt)
        => new()
        {
            SourceDocumentId = sourceDocumentId,
            Target = target.Length <= MaxTargetLength ? target : target[..MaxTargetLength],
            ExtractedAt = extractedAt,
        };
}
