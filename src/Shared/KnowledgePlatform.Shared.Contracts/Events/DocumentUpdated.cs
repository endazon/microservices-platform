namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-06, UC-03: 文書管理サービスが発行するイベント（登録・更新時）
public record DocumentUpdated(
    Guid DocumentId,
    string Title,
    string Status,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset UpdatedAt);
