using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.PrivateNotes.List;

// FR-19, SC-19: 一覧（削除済みを含む）＋容量表示。
// 削除済み行の bytes は「完全削除で解放される容量」の表示にそのまま使える（ADR-0037 決定 20）。
internal static class ListPrivateNotesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            var notes = await db.PrivateNotes.Where(n => n.OwnerId == owner)
                .OrderByDescending(n => n.UpdatedAt).ToListAsync(ct);
            var docIds = notes.Select(n => n.DocumentId).ToList();
            var docs = await db.Documents.Where(d => docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

            var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
            await db.SaveChangesAsync(ct);

            // #1441, SC-19 主要素 1・2・5: 公開範囲・同期状態・タグ名の材料は**所有者ごとに 1 回**
            // 引く（資料ごとに引くと一覧が件数に比例して遅くなる）。
            var enrichment = await PrivateNoteEnrichment.LoadAsync(db, owner, docIds, now, ct);

            return Results.Ok(new PrivateNoteListResponse(
                new PrivateNoteUsageDto(used, quota.LimitBytes, quota.PercentOf(used)),
                notes.Select(n => enrichment.ToDto(n, docs.GetValueOrDefault(n.DocumentId)))
                    .ToList()));
        });
    }
}
