using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices.Reissue;

// ADR-0037 決定 15: **手動再発行**。旧トークンは即時に無効化される。
// 期限切れ・失効済みの端末に対しても本人操作として再発行できる（回復経路はこれだけである）。
internal static class ReissueSyncDeviceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/reissue", async (Guid id, HttpContext http, DocumentDbContext db,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var device = await SyncDeviceEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (device is null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            var (token, hash) = SyncTokens.Generate();
            device.Reissue(hash, now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync-token.reissue", owner, "granted",
                $"device={device.Id}");
            return Results.Ok(new SyncTokenIssuedResponse(device.Id, device.DeviceName, token,
                device.ExpiresAt));
        });
    }
}
