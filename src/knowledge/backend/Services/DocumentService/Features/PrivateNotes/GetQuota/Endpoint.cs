using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.GetQuota;

// FR-19, NFR-27: 管理者による上限の照会（既定 1 GB・最大 1 TB）。
internal static class GetPrivateNoteQuotaEndpoint
{
    internal static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/{ownerId}", async (string ownerId, DocumentDbContext db,
            CancellationToken ct) =>
        {
            var used = await PrivateNoteUsage.UsedBytesAsync(db, ownerId, ct);
            var quota = await db.PrivateNoteQuotas.FindAsync([ownerId], ct);
            var limit = quota?.LimitBytes ?? PrivateNoteQuota.DefaultLimitBytes;
            return Results.Ok(new PrivateNoteUsageDto(used, limit,
                limit <= 0 ? 100 : (int)(used * 100 / limit)));
        });
    }
}
