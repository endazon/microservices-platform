using DocumentService.Domain;
using DocumentService.Features.SyncConflicts.Get;
using DocumentService.Features.SyncConflicts.List;
using DocumentService.Features.SyncConflicts.Resolve;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncConflicts;

// FR-20, UC-11, SC-20 主要素 5, ADR-0037 決定 7, [[IADR-0352]], #1442:
// 同期競合の一覧・詳細・解決の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/SyncConflicts/<操作>/` に居る。
// **ここに残るのは 3 操作が共有するもの**だけである —— route group、所有者スコープの照会、
// 表示用の材料（題・Vault パス・端末名）の引き当て。
//
// **サーバは自動解決しない**（決定 7）。この群が提供するのは「利用者が選ぶための材料」と
// 「選んだ結果の適用」だけであり、**後勝ちで畳む口は存在しない**。
//
// **他人の競合・不在・解決済みは同じ 404**（存在ごと秘匿する。ADR-0036 D-04 と同じ向き）。
// ただし**解決の再実行だけは 409** である —— 自分の競合であることは判っており、
// 「もう解決済みである」ことを利用者へ返さないと、画面が同じ操作を繰り返す。
public static class SyncConflictEndpoints
{
    public static IEndpointRouteBuilder MapSyncConflictEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/private-notes/conflicts").WithTags("PrivateNotes")
            .RequireAuthorization();

        ListSyncConflictsEndpoint.Map(g);
        GetSyncConflictEndpoint.Map(g);
        ResolveSyncConflictEndpoint.Map(g);

        return app;
    }

    // 未解決の競合だけを引く（一覧・`syncState` の導出・成功 push の閉じ込みが同じ述語を見る）。
    internal static IQueryable<SyncConflict> Unresolved(DocumentDbContext db, string owner)
        => db.SyncConflicts.Where(c => c.OwnerId == owner && c.ResolvedAt == null);

    // 所有者スコープの照会。**他人の競合は null**（呼び出し側で 404 になる）。
    internal static async Task<SyncConflict?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var conflict = await db.SyncConflicts.FindAsync([id], ct);
        return conflict is not null && conflict.OwnerId == owner ? conflict : null;
    }

    // SC-20 主要素 5: 一覧・詳細が共有する見出し（題・Vault パス・端末名）。
    // 題は `Document`、Vault パスは `PrivateNote`、端末名は `SyncDevice` にあり、**競合の行には無い**
    // （複写すると改名・リネームで古い値が残る）。
    internal static SyncConflictSummaryDto ToSummary(SyncConflict c, string title, string vaultPath,
        string deviceName)
        => new(c.Id, c.DocumentId, title, vaultPath, c.DetectedAt, c.DeviceId, deviceName,
            c.LocalBaseVersion, c.ServerVersion);

    // 表示の縮退: 文書の複製・端末が引けない場合も**例外にしない**（競合そのものは実在する）。
    // `PrivateNoteEnrichment` が題と版でやっているのと同じ端の判断である。
    internal static async Task<(Dictionary<Guid, Document> Docs, Dictionary<Guid, PrivateNote> Notes,
        Dictionary<Guid, SyncDevice> Devices)> LoadLabelsAsync(DocumentDbContext db,
        IReadOnlyCollection<Guid> documentIds, IReadOnlyCollection<Guid> deviceIds,
        CancellationToken ct)
    {
        var docs = await db.Documents.Where(d => documentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);
        var notes = await db.PrivateNotes.Where(n => documentIds.Contains(n.DocumentId))
            .ToDictionaryAsync(n => n.DocumentId, ct);
        var devices = await db.SyncDevices.Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);
        return (docs, notes, devices);
    }
}
