namespace WikiService.Domain;

// FR-13, UC-07, ADR-0011: Wiki ページエンティティ（文書管理からの同期）
public class WikiPage
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DocumentId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? MarkdownUri { get; private set; }
    public string Status { get; private set; } = WikiPageStatus.Active;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public DateTimeOffset SyncedAt { get; private set; } = DateTimeOffset.UtcNow;

    // IADR-0021: Wiki.js 上の安定パス（DocumentId 由来）。同期 push と認可プロキシの本文取得で共有する
    // 単一の正準パス。タイトル由来の Slug は人間可読の索引・メタデータ用途に留める。
    public string WikiPath => PathFor(DocumentId);

    // Issue #88: 削除・アーカイブはメタデータ未同期でも Wiki.js 側へ伝播する必要があるため、
    // DocumentId から正準パスを導出できるようにする（WikiPath と同一の導出規則）。
    public static string PathFor(Guid documentId) => $"doc/{documentId}";

    // UC-07, FR-13, IADR-0331: `PathFor` の逆写像。Wiki.js が返した検索ヒットのパスから台帳の行を
    // 引き当てるために要る。**導出規則を 2 箇所に散らさない**ため `PathFor` の隣に置く。
    // 先頭スラッシュの有無・大小文字は吸収し、`doc/<guid>` 以外の形（人手で作られたページ・
    // 別の名前空間）は false を返す —— **台帳に足場を持たないページは ABAC で判定できないので落とす。**
    public static bool TryParseDocumentId(string? path, out Guid documentId)
    {
        documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Trim().Trim('/').Split('/');
        return segments.Length == 2
            && segments[0].Equals("doc", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out documentId);
    }

    private WikiPage() { }

    public static WikiPage CreateFromDocument(Guid documentId, string title,
        string? markdownUri, Dictionary<string, string> attributes, List<string> tags)
        => new()
        {
            DocumentId = documentId,
            Title = title,
            Slug = ToSlug(title),
            MarkdownUri = markdownUri,
            Attributes = attributes,
            Tags = tags,
        };

    public void Sync(string title, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags)
    {
        Title = title;
        Slug = ToSlug(title);
        MarkdownUri = markdownUri;
        Attributes = attributes;
        Tags = tags;
        // Issue #88: 再公開でアーカイブ状態を解除する（アーカイブは可逆）。
        Status = WikiPageStatus.Active;
        SyncedAt = DateTimeOffset.UtcNow;
    }

    // Issue #88: アーカイブ（非公開化）。ゲートウェイの一覧・個別取得から不可視になる。
    public void Archive()
    {
        Status = WikiPageStatus.Archived;
        SyncedAt = DateTimeOffset.UtcNow;
    }

    private static string ToSlug(string title)
        => System.Text.RegularExpressions.Regex
            .Replace(title.ToLowerInvariant().Trim(), @"[^\w\d]+", "-")
            .Trim('-');
}

public static class WikiPageStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
}
