using MassTransit;

namespace Knowledge.Contracts.Events;

// FR-06, UC-03: 文書管理サービスが発行するイベント（登録・更新時）
// FR-14, IADR-0059: knowledge ユニット固有の契約。MessageUrn を旧名前空間に固定し wire 後方互換を維持する。
[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:DocumentUpdated")]
public record DocumentUpdated(
    Guid DocumentId,
    string Title,
    string Status,
    string? MarkdownUri,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset UpdatedAt);
