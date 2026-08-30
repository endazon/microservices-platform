using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices.Revoke;

// ADR-0037 決定 13: 個別失効。
internal static class RevokeSyncDeviceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var device = await SyncDeviceEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (device is null) return Results.NotFound();

            device.Revoke(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync-token.revoke", owner, "granted",
                $"device={device.Id}");
            return Results.NoContent();
        });
    }
}
