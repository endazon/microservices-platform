using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices.RevokeAll;

// ADR-0037 決定 13: **全端末の一括失効**（端末紛失時の防御。どの端末か特定できない場面用）。
internal static class RevokeAllSyncDevicesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/revoke-all", async (HttpContext http, DocumentDbContext db,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var devices = await db.SyncDevices
                .Where(d => d.OwnerId == owner && d.RevokedAt == null).ToListAsync(ct);
            foreach (var device in devices) device.Revoke(now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync-token.revoke-all", owner, "granted",
                $"count={devices.Count}");
            // #451-a: 契約型で返す（匿名型だと BFF・画面・openapi のどれとも突き合わない）。
            return Results.Ok(new RevokeAllSyncDevicesResponse(devices.Count));
        });
    }
}
