namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-01, FR-02, UC-04: データソース連携サービスが発行するイベント
public record RawDocumentFetched(
    Guid FetchId,
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string StorageUri,
    string ContentType,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset FetchedAt);
