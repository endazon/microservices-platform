namespace DocumentService.Api.Domain;

// FR-06, ADR-0002: 正規化文書エンティティ（DB per Service）
public class Document
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = DocumentStatus.Draft;
    public string? MarkdownUri { get; private set; }
    public string? OriginalUri { get; private set; }
    public string? ContentType { get; private set; }
    public int Version { get; private set; } = 1;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Document() { }

    public static Document Create(string title, string? originalUri, string? contentType,
        Dictionary<string, string>? attributes = null, List<string>? tags = null)
        => new()
        {
            Title = title,
            OriginalUri = originalUri,
            ContentType = contentType,
            Attributes = attributes ?? [],
            Tags = tags ?? [],
        };

    public void Update(string title, Dictionary<string, string> attributes, List<string> tags)
    {
        Title = title;
        Attributes = attributes;
        Tags = tags;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public void SetMarkdownUri(string uri)
    {
        MarkdownUri = uri;
        Status = DocumentStatus.Normalized;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Publish()
    {
        Status = DocumentStatus.Published;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public static class DocumentStatus
{
    public const string Draft = "draft";
    public const string Normalized = "normalized";
    public const string Published = "published";
}
