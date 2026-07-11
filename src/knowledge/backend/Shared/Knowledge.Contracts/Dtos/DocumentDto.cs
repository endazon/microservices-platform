namespace Knowledge.Contracts.Dtos;

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

// SC-03, FR-06: 文書本文（正規化 Markdown）の取得結果。BFF が ABAC 判定後にオブジェクト
// ストレージ（storage://）から読み取り、閲覧を許可された呼び出し元にのみ払い出す。
public record DocumentContentDto(Guid Id, string Title, string Markdown, string? SourceUri);

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
