namespace GraphService.Domain;

// FR-17, FR-13, ADR-0033 決定 8, [[IADR-0522]] (#1396): **Wiki のリンク**の名前。
//
// Wiki.js 上の文書ページの正準パスは `doc/<DocumentId>` である（WikiService の `WikiPage.PathFor`。
// 別サービスなので参照せず、形だけを揃える）。本文に書かれた `/doc/<ID>`・`/en/doc/<ID>`・
// `https://wiki.example/doc/<ID>` は、題名ではなく**文書 ID で**相手を指している。
// リンク先の名前はこの形（`doc/` ＋ 小文字の GUID）に揃え、解決は `LinkTargetMatcher` が ID で行う。
public static class WikiDocumentPath
{
    private const string Prefix = "doc/";

    public static string Format(Guid documentId) => $"{Prefix}{documentId:D}";

    // 正規化済みの名前（`Format` の形）か。
    public static bool TryParse(string? target, out Guid documentId)
    {
        documentId = Guid.Empty;
        return target is not null
            && target.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(target[Prefix.Length..], "D", out documentId);
    }

    // リンクの宛先（パス部分。クエリ・断片は除去済み）の**最後の 2 セグメント**が `doc/<GUID>` なら、その ID。
    // 先頭のスラッシュ・ロケール等の接頭セグメントは問わない。
    public static bool TryParseLinkPath(string path, out Guid documentId)
    {
        documentId = Guid.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && segments[^2].Equals("doc", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[^1], out documentId);
    }
}
