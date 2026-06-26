namespace KnowledgePlatform.Shared.Contracts.Events;

// FR-01, FR-02, UC-04: データソース連携サービスが発行するイベント
public record RawDocumentFetched(
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string StorageUri,
    DateTimeOffset FetchedAt);
