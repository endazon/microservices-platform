using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.PrivateNotes.SetQuota;

// FR-19, NFR-27: 管理者による上限の変更（既定 1 GB・最大 1 TB）。
// ［2026-10-30 / #2330］0 を許す（計画 2026-10-24 の裁定）。SC-17 の欄から BFF 経由で呼ばれる（入力の型は契約へ移した）。
internal static class SetPrivateNoteQuotaEndpoint
{
    internal static void Map(RouteGroupBuilder admin)
    {
        admin.MapPut("/{ownerId}", async (string ownerId, SetPrivateNoteQuotaRequest req,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, ownerId, now, ct);
            try
            {
                quota.SetLimit(req.LimitBytes, now);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limitBytes"] = [ex.Message]
                });
            }
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.quota.set", ownerId, "granted",
                $"limitBytes={req.LimitBytes}");
            var used = await PrivateNoteUsage.UsedBytesAsync(db, ownerId, ct);
            return Results.Ok(new PrivateNoteUsageDto(used, quota.LimitBytes,
                quota.PercentOf(used)));
        });
    }
}
