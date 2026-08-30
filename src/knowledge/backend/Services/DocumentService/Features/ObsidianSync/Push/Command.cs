namespace DocumentService.Features.ObsidianSync.Push;

// FR-20, ADR-0037 決定 8: **1 編集 = 1 版**。オフラインで 10 回編集して 1 回同期した場合も、
// `Edits` に 10 要素を載せれば 10 版として刻まれる。
public record SyncEditRequest(string? Content, DateTimeOffset? EditedAt = null,
    string? ChangeNote = null);

public record PushNoteRequest(
    Guid? NoteId,
    string VaultPath,
    string Title,
    int? BaseVersion,
    List<SyncEditRequest> Edits);

public record PushNoteResponse(Guid NoteId, int Version, string ContentHash, long Bytes);
