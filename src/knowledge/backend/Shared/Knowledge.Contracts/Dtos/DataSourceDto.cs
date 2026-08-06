namespace Knowledge.Contracts.Dtos;

// FR-01, FR-02, UC-04, SC-06: データソースの参照用 DTO（BFF ↔ SPA 契約）。
// DataSourceService のドメインエンティティ（/datasources）と JSON 互換。BFF は本 DTO で型付けして
// SPA へ返す（サービス実装に SPA を依存させない）。ConnectionUri はコネクタ設定であり秘匿情報を
// 含み得るため、SPA へは登録済みの値のみ表示し、SPA からの秘密の埋め込みは行わない（IADR）。
// SC-06（planning#200 / 利用者裁定 2026-08-05 質問票 第12回 Q15）: NextSyncAt は「次に取り込まれるのはいつか」
// に答えるための**共通間隔の次回実行時刻**であり、**全ソースで同じ値**になる。ソース別スケジュール
// （cron 等）はモデル化しない（裁定）。定期同期が無効なときは null（＝次回は無い）。導出値のため永続化しない。
public record DataSourceDto(
    Guid Id,
    string Name,
    string SourceType,
    string ConnectionUri,
    string Status,
    DateTimeOffset? LastSyncedAt,
    Dictionary<string, string> Config,
    Dictionary<string, string> DefaultAttributes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextSyncAt = null);

// FR-01, UC-04, SC-06: データソース登録リクエスト（BFF 経由）。DataSourceService の CreateDataSourceRequest
// と JSON 互換。DefaultAttributes 未指定時はサービス側が機密区分 internal をフェイルセーフ補完する。
public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);
