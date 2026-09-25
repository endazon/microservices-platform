namespace DocumentService.Features.ObsidianSync.Push;

// FR-20, ADR-0037 決定 8: **1 編集 = 1 版**。オフラインで 10 回編集して 1 回同期した場合も、
// `Edits` に 10 要素を載せれば 10 版として刻まれる。
public record SyncEditRequest(string? Content, DateTimeOffset? EditedAt = null,
    string? ChangeNote = null);

// FR-20, SC-20 主要素 5, ADR-0105 決定 3, ADR-0110（planning#652 の裁定 1）, [[IADR-0464]] (#1521):
// `SourceNoteId` は「両方を残す」の写しを作るとき、プラグインが**元のノートの ID** を添える任意項目である。
// 新規作成（`NoteId` が null）のときだけ読み、同じ所有者の個人資料を指すときだけそのタグを写す。
// 既定値を持つのは後方互換のため（従前のプラグインは送らない＝従来どおりタグは空）。
public record PushNoteRequest(
    Guid? NoteId,
    string VaultPath,
    string Title,
    int? BaseVersion,
    List<SyncEditRequest> Edits,
    Guid? SourceNoteId = null);

public record PushNoteResponse(Guid NoteId, int Version, string ContentHash, long Bytes);
