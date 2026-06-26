namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-12, UC-06: 変換サービスが正規化完了時に発行するイベント
public record DocumentNormalized(
    Guid DocumentId,
    Guid SourceId,
    string Title,
    string MarkdownUri,
    List<string> AssetUris,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset NormalizedAt);
