namespace DocumentService.Features.ObsidianSync.Manifest;

// FR-20, ADR-0037 決定 14: マニフェストの 1 行。**削除済みも `Deleted=true` で現れる**
// （サーバ側の削除をプラグインが検知できるようにするため。KB が唯一の正である）。
public record SyncManifestEntry(Guid NoteId, string Title, string VaultPath, int Version,
    string? ContentHash, bool Deleted, DateTimeOffset UpdatedAt);
