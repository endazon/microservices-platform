namespace DocumentService.Features.ObsidianSync.Move;

// FR-20, ADR-0037 決定 2・7, [[IADR-0353]] 決定 1・2: リネーム（`vaultPath` の更新）の要求と応答。
//
// **本文は運ばない**（名前だけを動かす）。`Version` はクライアントが最後に見た版であり、
// 楽観ロックのためだけに使う —— **リネームは版を進めない**（本文が変わっていないため。決定 2）。
public record MoveNoteRequest(string VaultPath, int? Version);

public record MoveNoteResponse(Guid NoteId, string VaultPath, int Version,
    DateTimeOffset UpdatedAt);
