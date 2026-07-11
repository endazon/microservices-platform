using MassTransit;

namespace Knowledge.Contracts.Events;

// FR-01, FR-02, UC-04: データソース連携サービスが発行するイベント
// FR-14, IADR-0059: knowledge ユニット固有の契約。MessageUrn を旧名前空間に固定し wire 後方互換を維持する。
[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:RawDocumentFetched")]
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
