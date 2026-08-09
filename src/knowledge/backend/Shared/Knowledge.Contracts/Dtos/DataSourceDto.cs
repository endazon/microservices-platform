namespace Knowledge.Contracts.Dtos;

// FR-01, FR-02, UC-04, SC-06: データソースの同期健全性の定数（契約側の単一情報源）。
// SC-06（planning#200 / 利用者裁定 2026-08-05 質問票 第12回 Q14）:
// 「継続失敗」のしきい値は**再試行上限に達した時点**である（hi-fi の「3/5」に倣えば 5 回連続失敗）。
// hi-fi の「3/5」は「5 回中 3 回目」という進捗表示であって、しきい値が 3 という意味ではない。
// **しきい値と再試行上限を別々の定数として持たない** —— 2 つ持つと片方が黙って古くなる。
public static class DataSourceSyncHealth
{
    // 再試行上限 ＝ 継続失敗アラートのしきい値。連続失敗回数がこの値に達した時点で発報する。
    public const int DefaultRetryLimit = 5;
}

// FR-01, FR-02, UC-04, SC-06: データソースの参照用 DTO（BFF ↔ SPA 契約）。
// DataSourceService のドメインエンティティ（/datasources）と JSON 互換。BFF は本 DTO で型付けして
// SPA へ返す（サービス実装に SPA を依存させない）。ConnectionUri はコネクタ設定であり秘匿情報を
// 含み得るため、SPA へは登録済みの値のみ表示し、SPA からの秘密の埋め込みは行わない（IADR）。
// SC-06（planning#200 / 利用者裁定 2026-08-05 質問票 第12回 Q15）: NextSyncAt は「次に取り込まれるのはいつか」
// に答えるための**共通間隔の次回実行時刻**であり、**全ソースで同じ値**になる。ソース別スケジュール
// （cron 等）はモデル化しない（裁定）。定期同期が無効なときは null（＝次回は無い）。導出値のため永続化しない。
//
// SC-06（同 Q14 / issue #537）: 同期健全性 3 項目（ConsecutiveFailureCount / RetryLimit /
// LastSyncError・LastSyncErrorAt）を持つ。従前の Status は active / disabled の 2 値のみで健全性を
// 表現できず、UC-04 例外フローの「継続失敗はアラートする」という確定済みの要求を画面もアラートも
// 満たせなかった。**琥珀（警告色）はここへ充てる**（IADR-0127 決定 2 が予約していた充て先）。
// **IsUnhealthy のような導出フラグは持たせない** —— 判定（ConsecutiveFailureCount >= RetryLimit）は
// 画面側で行える。契約に持たせると同じ判断が 2 箇所に立ち、片方が古くなる。
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
    DateTimeOffset? NextSyncAt = null,
    // UC-04 例外フロー: 連続同期失敗回数。同期の完全成功で 0 へ戻る。
    int ConsecutiveFailureCount = 0,
    // 再試行上限（＝継続失敗のしきい値）。画面が「3/5」の分母を契約から得るために返す
    // （画面へ定数を複写すると IADR-0127 決定 2 が禁じた「契約から導出できない表示」に戻る）。
    int RetryLimit = DataSourceSyncHealth.DefaultRetryLimit,
    // 直近の同期エラー。メッセージは秘密を含み得るためサービス側でマスクしてから保存する。
    string? LastSyncError = null,
    DateTimeOffset? LastSyncErrorAt = null);

// FR-01, UC-04, SC-06: データソース登録リクエスト（BFF 経由）。DataSourceService の CreateDataSourceRequest
// と JSON 互換。DefaultAttributes 未指定時はサービス側が機密区分 internal をフェイルセーフ補完する。
public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);

// FR-01, UC-04, SC-06（Q16 / issue #534）: データソース更新リクエスト（全置換 = PUT）。
// 従前は更新の口が無く、登録済みソースの変更が「削除→再登録」でしかできなかった。削除→再登録は
// **ID と履歴を切る**（認証情報のローテーションのたびに文書の出所の追跡が切れるのは監査上受け入れがたい）。
// **Id / CreatedAt / LastSyncedAt / 同期健全性は更新の対象外**である —— 更新で履歴を巻き戻せてはならない。
// **Config / DefaultAttributes を省略した要求はサービスが 400 で拒否する**（AI レビュー 🟡 / #627）。
// PUT は全置換なので、省略を受理すると「送り忘れ」で秘密（apiToken 等）が黙って消える。
// 消したいときは {} を明示する。一部だけ変えるなら PATCH を使う。
public record UpdateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);

// FR-01, UC-04, SC-06（Q16 / issue #534）: データソース部分更新リクエスト（PATCH）。
// **null の項目は現状維持**である。接続先だけ・認証情報だけを差し替える日常運用を、
// 他項目を読んで書き戻す往復なしに行えるようにする（往復させると応答のマスク済みの値〔***〕を
// 書き戻して秘密を破壊する事故が起きる）。
public record PatchDataSourceRequest(
    string? Name = null,
    string? SourceType = null,
    string? ConnectionUri = null,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);
