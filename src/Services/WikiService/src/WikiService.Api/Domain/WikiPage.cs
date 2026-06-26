namespace WikiService.Api.Domain;

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
