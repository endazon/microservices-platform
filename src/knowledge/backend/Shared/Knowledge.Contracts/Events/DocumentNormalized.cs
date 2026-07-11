using MassTransit;

namespace Knowledge.Contracts.Events;

// FR-12, UC-06: 変換サービスが正規化完了時に発行するイベント
// FR-14, IADR-0059: knowledge ユニット固有の契約。MessageUrn を旧名前空間に固定し wire 後方互換を維持する。
[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:DocumentNormalized")]
public record DocumentNormalized(
    Guid DocumentId,
    Guid SourceId,
    string Title,
    string MarkdownUri,
    List<string> AssetUris,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    DateTimeOffset NormalizedAt);
