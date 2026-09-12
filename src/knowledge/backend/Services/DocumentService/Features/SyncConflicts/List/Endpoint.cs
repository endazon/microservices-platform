using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncConflicts.List;

// FR-20, SC-20 主要素 5, ADR-0037 決定 7, #1442: 未解決の競合の一覧（検出日時の新しい順）。
//
// 🔴 **本文は返さない。** 2 ペイン差分の材料（ローカル版・サーバ版の本文）は詳細だけが返す ——
// 一覧で全件の本文を運ぶと、競合が数件でも応答が資料本文の合計になる。
internal static class ListSyncConflictsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            var conflicts = await SyncConflictEndpoints.Unresolved(db, owner)
                .OrderByDescending(c => c.DetectedAt).ToListAsync(ct);

            // N+1 を避ける: 題・Vault パス・端末名はまとめて引く。
            var (docs, notes, devices) = await SyncConflictEndpoints.LoadLabelsAsync(db,
                [.. conflicts.Select(c => c.DocumentId).Distinct()],
                [.. conflicts.Select(c => c.DeviceId).Distinct()], ct);

            return Results.Ok(conflicts.Select(c => SyncConflictEndpoints.ToSummary(c,
                docs.GetValueOrDefault(c.DocumentId)?.Title ?? string.Empty,
                notes.GetValueOrDefault(c.DocumentId)?.VaultPath ?? string.Empty,
                devices.GetValueOrDefault(c.DeviceId)?.DeviceName ?? string.Empty)).ToList());
        });
    }
}
