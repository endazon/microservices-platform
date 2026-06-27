namespace KnowledgePlatform.Shared.Contracts.Dtos;

public class DocumentDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string? MarkdownUri { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
