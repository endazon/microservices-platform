namespace Knowledge.Contracts.Dtos;

// FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0054 決定 4, #451: 個人資料（private-note）と
// 同期端末の契約 DTO。**BFF（Knowledge.Bff.Endpoints）が画面へ配る面の型**である。
//
// 形は後段 DocumentService（`PrivateNoteEndpoints` / `SyncDeviceEndpoints`）の応答に一致させる。
// **後段の型を参照しない**（サービスの内部型はユニット外から見えない。`DocumentBffEndpoints` が
// `IsPrivateNote` を自前で持つのと同じ理由・同じ形）。一致は 2 つの機械検査が保つ ——
// `scripts/check-openapi-dto-drift.js`（openapi の schemas とプロパティ集合）と
// `scripts/check-contract-schema.js`（契約スナップショットの後方互換）。
//
// 🔴 **主体（所有者）を運ぶ口を作らない。** 後段は主体を JWT からしか採らず（`SubjectOf`）、
// 台帳 `PrivateNote.OwnerId` で絞る。DTO に `ownerId` を置くと「誰の資料か」を要求側が指定できる形に
// 見えてしまい、いずれ実装がそれを読む。**個人資料は本人のみ**（ADR-0036）を型でも守る。

// FR-19, SC-19: 個人資料 1 件（台帳の投影。**本文は含まない** —— 本文編集は Obsidian 経路のみ。ADR-0046）。
//
// `Bytes` は**最新版のバイト数**である。版履歴は容量に算入しない（ADR-0037 決定 16。
// 台帳が最新版しか持たないため、算入しようがない＝規則を型で守っている）。
// `Deleted` が真のとき `PurgeAt` が完全削除の期限であり、SC-19 の「残り日数」と警告色の根拠になる。
public record PrivateNoteDto(
    Guid Id,
    string Title,
    string VaultPath,
    int Version,
    long Bytes,
    string? ContentHash,
    bool IncludeInSearch,
    bool IncludeInGraph,
    bool IncludeInAi,
    bool Deleted,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? PurgeAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // #1441（planning#614 の裁定。SC-19 主要素 1・2・5・6）: 公開範囲・同期状態・タグ。
    // 状態は文字列（enum にしない。IADR-0131 論点 C）。値集合は `PrivateNoteVisibilityValues` /
    // `PrivateNoteSyncStates` が持つ。**指定先（共有相手）は本 DTO に載せず `DocumentShareDto`
    // （`GET /bff/private-notes/{id}/shares`）で引く**（ADR-0098 決定 1。#1445）。`Tags` はタグ辞書の表示名。
    string Visibility,
    int SharedUserCount,
    int SharedGroupCount,
    string SyncState,
    List<string> Tags);

// #1441, SC-19 主要素 2: 公開範囲の 3 状態（契約の値集合。後段の導出とフロントの描き分けが同じ定数を見る）。
public static class PrivateNoteVisibilityValues
{
    public const string Private = "private";
    public const string Users = "users";
    public const string Groups = "groups";
}

// #1441, SC-19 主要素 5, ADR-0037 決定 3・4・7: 同期状態の 3 状態。
public static class PrivateNoteSyncStates
{
    public const string Conflict = "conflict";
    public const string Target = "target";
    public const string Excluded = "excluded";
}

// FR-19, SC-19, ADR-0037 決定 16・17: 保存容量の使用状況。
//
// **算入するのは最新版と論理削除済み（90 日保管中）**であり、版履歴は算入しない。
// SC-19 は「うち削除済み」の内訳も示すと定めているが、**内訳は一覧の削除済み行の `Bytes` から
// 画面が合算する**（後段が持つのは台帳の行だけで、内訳の項目は存在しない）。
// `Percent` は 80 / 95 / 100 の段階警告の判定に画面が使う値である。
public record PrivateNoteUsageDto(long UsedBytes, long LimitBytes, int Percent);

// FR-19, SC-19: 一覧（削除済みを含む）と容量表示を 1 応答で返す。
// **画面が 2 回呼ばずに済むのが BFF の役目**であり、後段も 1 応答で返している。
public record PrivateNoteListResponse(PrivateNoteUsageDto Usage, List<PrivateNoteDto> Notes);

// FR-19, SC-19: 新規作成（**タイトルのみ。本文は受け取らない**）。
// `VaultPath` 省略時は後段が `<タイトル>.md` を採る。
public record CreatePrivateNoteRequest(string Title, string? VaultPath = null);

// FR-19, SC-19, ADR-0037 決定 20: 完全削除（即時・復元不可）。**単票も一括も同じ口**である
// （要素数の差でしかない。SC-19 は「複数選択しての一括削除」を必須としている）。
public record PurgePrivateNotesRequest(List<Guid> Ids);

// FR-19, SC-19: 完全削除の結果。`FreedBytes` は SC-19 の確認ダイアログが示す「解放される容量」を
// **実行後に確定値として返すもの**である（③が無いと利用者は取り返しのつかない操作の効果を判断できない）。
public record PurgePrivateNotesResponse(int PurgedCount, long FreedBytes);

// FR-19, SC-19, ADR-0037 決定 19・20: 論理削除の結果。
//
// 🔴 `CapacityFreed` は**常に false** である。「削除しても容量は空かない（90 日保管）」という
// SC-19 の固定文言の根拠を、散文ではなく**機械可読な形で**画面へ渡す。
public record PrivateNoteDeletedResponse(
    DateTimeOffset? DeletedAt, DateTimeOffset? PurgeAt, bool CapacityFreed);

// FR-19, SC-20: 露出 3 トグル（横断検索 / ナレッジグラフ / AI 入力）。
// **既定はいずれも OFF・3 つは独立**である。ON の消費側配線は未了のため、現時点は保存のみ（IADR-0270 決定 5）。
public record UpdateExposureRequest(bool IncludeInSearch, bool IncludeInGraph, bool IncludeInAi);

// FR-20, SC-20: 同期端末 1 件。**トークンは平文もハッシュも載らない。**
// `Active` は「未失効かつ期限内」であり、`ExpiresAt` との差から画面が
// 「有効（残り日数）／期限切れ間近（7 日以内）／期限切れ／失効」を描き分ける。
public record SyncDeviceDto(
    Guid Id,
    string DeviceName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    DateTimeOffset? LastSyncAt,
    bool Active);

// FR-20, SC-20, ADR-0037 決定 11: 端末登録（同期トークンの発行）。
public record CreateSyncDeviceRequest(string DeviceName);

// FR-20, SC-20, ADR-0037 決定 12・15: 発行・再発行の応答。
//
// 🔴 **`Token`（平文）が現れるのはこの型だけである。** 保存されるのはハッシュのみで、
// 一覧（`SyncDeviceDto`）にも他のどの応答にも平文は載らない。SC-20 の
// 「発行直後に一度だけ表示し、再表示できない（再発行のみ可）」を型で担保する。
// 有効期限は 30 日・**更新は手動再発行のみ**（自動リフレッシュの口を持たない）。
public record SyncTokenIssuedResponse(
    Guid DeviceId, string DeviceName, string Token, DateTimeOffset ExpiresAt);

// FR-20, SC-20, ADR-0037 決定 13: 全端末の一括失効の結果（端末紛失時の防御）。
public record RevokeAllSyncDevicesResponse(int RevokedCount);

// ── #1442（planning#614 の裁定。SC-20 主要素 3・5）: 同期対象範囲と競合 ──
// 同期履歴（主要素 6）の型は `SyncHistoryDto.cs`（ADR-0099。#1446）。

// FR-20, SC-20 主要素 3: 同期対象フォルダ 1 件と配下の資料数・最終同期日時。
public record SyncTargetFolderDto(string Path, int NoteCount, DateTimeOffset? LastSyncAt);

// FR-20, SC-20 主要素 3, ADR-0037 決定 3・4: 本人の同期設定。`TargetFolders` が空なら全資料が対象。
public record SyncSettingsDto(List<SyncTargetFolderDto> TargetFolders, DateTimeOffset? UpdatedAt);

// FR-20, SC-20 主要素 3: 同期対象フォルダの置き換え（全量）。
public record UpdateSyncSettingsRequest(List<string> TargetFolders);

// FR-20, SC-20 主要素 5, ADR-0037 決定 7: 未解決の同期競合 1 件（一覧用。本文は含まない）。
public record SyncConflictSummaryDto(
    Guid Id,
    Guid NoteId,
    string Title,
    string VaultPath,
    DateTimeOffset DetectedAt,
    Guid DeviceId,
    string DeviceName,
    int LocalBaseVersion,
    int ServerVersion);

// FR-20, SC-20 主要素 5: 競合の詳細（2 ペイン差分の材料）。本文は詳細だけが返す。
public record SyncConflictDetailDto(
    Guid Id,
    Guid NoteId,
    string Title,
    string VaultPath,
    DateTimeOffset DetectedAt,
    Guid DeviceId,
    string DeviceName,
    int LocalBaseVersion,
    int ServerVersion,
    string LocalContent,
    string ServerContent);

// FR-20, SC-20 主要素 5, ADR-0037 決定 7: 競合の解決。値は `SyncConflictResolutions` の 3 つ。
// **自動解決（後勝ち）の値は存在しない。**
public record ResolveSyncConflictRequest(string Resolution);

// FR-20, SC-20 主要素 5: 解決の結果。`CreatedNoteId` は `both` で作った別名資料の ID（それ以外は null）。
public record ResolveSyncConflictResponse(
    Guid ConflictId, Guid NoteId, string Resolution, int NoteVersion, Guid? CreatedNoteId);

// #1442: 解決の 3 択（利用者が選ぶ値）。`Client` は契約の値ではなく、プラグイン側で解決されて
// 正しい baseVersion の push が通ったときに後段が競合を閉じる記録用の値である。
public static class SyncConflictResolutions
{
    public const string Local = "local";
    public const string Server = "server";
    public const string Both = "both";
    public const string Client = "client";

    public static readonly string[] Selectable = [Local, Server, Both];
    public static bool IsSelectable(string value) => Selectable.Contains(value);
}
