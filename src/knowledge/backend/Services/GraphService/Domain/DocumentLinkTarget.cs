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

    // ［[[IADR-0522]] / #1396］**リンクを辺へ作り直すのに要る残りの 3 つ**（構文の別・明示型・アンカー）。
    // 相手が後から届いた・改名された・曖昧が解けたとき、起点の本文を読み直さずに辺を作り直すために持つ
    // （本文の再読込は ADR-0050 決定 3 の契機を増やす）。
    //
    // 🔴 **移行前の行は `Kind` が null である。** その行からは辺の型が決まらない（`[[a#h]]` は cites、
    // `![[a]]` は embeds）ので、null の行を持つ起点は作り直さない（`LinkEdgeSynchronizer.RebuildAsync`）。
    // 次に本文が変わったときの再取り込みで埋まる。
    public const int MaxNameLength = 200;
    public string? Kind { get; private set; }
    public string? ExplicitTypeName { get; private set; }
    public string? Anchor { get; private set; }

    private DocumentLinkTarget() { }

    // [[IADR-0522]]: 1 リンク 1 行。名前・構文の別・明示型・アンカーの組で重複を落とすのは呼び出し側。
    public static DocumentLinkTarget Create(Guid sourceDocumentId, ObsidianLink link, DateTimeOffset extractedAt)
    {
        var row = Create(sourceDocumentId, link.Target, extractedAt);
        row.Kind = link.Kind.ToString();
        row.ExplicitTypeName = Cut(link.ExplicitTypeName);
        row.Anchor = Cut(link.Anchor);
        return row;
    }

    // 保存した行からリンクを復元する。**移行前の行（`Kind` が null・未知の値）は null。**
    public ObsidianLink? ToLink()
        => Enum.TryParse<ObsidianLinkKind>(Kind, ignoreCase: false, out var kind)
            && Enum.IsDefined(kind)
            ? new ObsidianLink(Target, Anchor, ExplicitTypeName, kind)
            : null;

    private static string? Cut(string? value)
        => value is { Length: > MaxNameLength } ? value[..MaxNameLength] : value;

    public static DocumentLinkTarget Create(Guid sourceDocumentId, string target, DateTimeOffset extractedAt)
        => new()
        {
            SourceDocumentId = sourceDocumentId,
            Target = target.Length <= MaxTargetLength ? target : target[..MaxTargetLength],
            ExtractedAt = extractedAt,
        };
}
