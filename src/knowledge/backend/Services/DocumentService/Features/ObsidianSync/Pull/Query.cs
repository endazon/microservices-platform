namespace DocumentService.Features.ObsidianSync.Pull;

// FR-20: pull の応答。**本文をそのまま運ぶ**（個人資料の本文が端末へ出る egress の実行点。
// 許容条件 4 により、実行記録は監査ログへ残す）。
public record PullNoteResponse(Guid NoteId, string Title, string VaultPath, int Version,
    string? ContentHash, bool Deleted, string Content);
