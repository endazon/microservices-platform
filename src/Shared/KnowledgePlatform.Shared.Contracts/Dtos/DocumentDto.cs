namespace KnowledgePlatform.Shared.Contracts.Dtos;

public class DocumentDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string? MarkdownUri { get; init; }
    // FR-06, UC-03: 現在の版番号
    public int Version { get; init; } = 1;
    public Dictionary<string, string> Attributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

// FR-06, UC-03: 文書の版スナップショット
public class DocumentVersionDto
{
    public Guid DocumentId { get; init; }
    public int Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string? MarkdownUri { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string? ChangeNote { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
