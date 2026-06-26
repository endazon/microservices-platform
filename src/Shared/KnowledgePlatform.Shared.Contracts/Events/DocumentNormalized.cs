namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-12, UC-06: 変換サービスが発行するイベント（pandoc + LLM 完了後）
public record DocumentNormalized(
    Guid DocumentId,
    string MarkdownStorageUri,
    DateTimeOffset NormalizedAt);
