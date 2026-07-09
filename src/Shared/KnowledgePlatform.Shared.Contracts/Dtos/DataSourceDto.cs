namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-01, FR-02, UC-04, SC-06: データソースの参照用 DTO（BFF ↔ SPA 契約）。
// DataSourceService のドメインエンティティ（/datasources）と JSON 互換。BFF は本 DTO で型付けして
// SPA へ返す（サービス実装に SPA を依存させない）。ConnectionUri はコネクタ設定であり秘匿情報を
// 含み得るため、SPA へは登録済みの値のみ表示し、SPA からの秘密の埋め込みは行わない（IADR）。
public record DataSourceDto(
    Guid Id,
    string Name,
    string SourceType,
    string ConnectionUri,
    string Status,
    DateTimeOffset? LastSyncedAt,
    Dictionary<string, string> Config,
    Dictionary<string, string> DefaultAttributes,
    DateTimeOffset CreatedAt);

// FR-01, UC-04, SC-06: データソース登録リクエスト（BFF 経由）。DataSourceService の CreateDataSourceRequest
// と JSON 互換。DefaultAttributes 未指定時はサービス側が機密区分 internal をフェイルセーフ補完する。
public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);
