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

    // UC-07, FR-13, IADR-0335: `PathFor` の逆写像。Wiki.js が返した検索ヒットのパスから台帳の行を
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

    // FR-13, [[IADR-0541]] (#1934): slug は呼び出し元（同期の受け口）が `SlugCandidates` から空きを選んで渡す。
    // 省略時は候補列の先頭（題名だけ由来）—— 一意性は保証しない（試験の種まき用）。
    public static WikiPage CreateFromDocument(Guid documentId, string title,
        string? markdownUri, Dictionary<string, string> attributes, List<string> tags, string? slug = null)
        => new()
        {
            DocumentId = documentId,
            Title = FitTitle(title),
            Slug = slug ?? SlugCandidates(title, documentId)[0],
            MarkdownUri = markdownUri,
            Attributes = attributes,
            Tags = tags,
        };

    public void Sync(string title, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags, string? slug = null)
    {
        Title = FitTitle(title);
        Slug = slug ?? SlugCandidates(title, DocumentId)[0];
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

    // slug 列の長さ（WikiDbContext の HasMaxLength と同じ値）。
    public const int SlugMaxLength = 500;

    // 🔴 FR-13, [[IADR-0554]] 決定 3 (#1966): 表題の列の長さ（WikiDbContext の HasMaxLength と同じ値）。
    // これを超える表題は保存の 22001 で再試行を使い切り DLQ へ入っていた。文字の境界で切り、印（…）を付けて収める。
    public const int TitleMaxLength = 500;

    private static string FitTitle(string title) => WikiText.FitColumn(title, TitleMaxLength);

    // 🔴 FR-13, [[IADR-0541]] 決定 1 (#1934): **slug の候補列。題名だけでは一意にならない**（同じ見出しのニュース・
    // 重複して作られた同名の規程が同じ slug へ写り、`IX_Pages_Slug` の 23505 で同期が恒久的に失敗した）。
    // ［2026-10-26 / #2108・[[IADR-0644]]］索引は一意でなくなった（ADR-0091 決定 1）。候補列は旧形式の URL が 1 件に決まるために残す。
    // 候補は [題名由来, 題名由来-文書IDの先頭8桁, 題名由来-文書ID 32桁]。**後ろほど一意で、末尾は文書 ID を丸ごと含む**ので、
    // 文書 ID の一意性（`IX_Pages_DocumentId`）から他の文書の末尾の候補とは重ならない。
    // 題名由来の部分が空（記号だけの題名）なら素の候補を飛ばし `doc-<8桁>` から始める（空の slug を作らない）。
    // **どれを採るか（他の文書が使っていない最初のもの・既存の slug の保持）は受け口が決める**（DB を引くため）。
    public static IReadOnlyList<string> SlugCandidates(string title, Guid documentId)
    {
        // [[IADR-0554]] 決定 3 (#1966): 題名だけ由来の候補も列の長さに収める（従来は接尾辞付きの候補だけを切っており、
        // 500 文字を超える題名は保存の 22001 で DLQ へ入った）。slug は値そのものが URL なので印は付けない。
        // `ToSlug` は元から両端の `-` を落とすので、切らない題名の slug は従来と同じ値のまま（試験で固定）。末尾の `-` を落とすのは切ったときだけ。
        var baseSlug = WikiText.FitColumn(ToSlug(title), SlugMaxLength, out var slugTruncated, mark: false);
        if (slugTruncated) baseSlug = baseSlug.TrimEnd('-');
        var full = documentId.ToString("N");
        var stem = baseSlug.Length == 0 ? "doc" : baseSlug;
        // 接尾辞（"-" + 32 桁）を足しても列の長さを超えないよう、題名由来の部分を切る。
        var room = SlugMaxLength - 1 - full.Length;
        if (stem.Length > room) stem = stem[..room].TrimEnd('-');
        var shortSuffixed = $"{stem}-{full[..8]}";
        var fullSuffixed = $"{stem}-{full}";
        return baseSlug.Length == 0
            ? [shortSuffixed, fullSuffixed]
            : [baseSlug, shortSuffixed, fullSuffixed];
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
