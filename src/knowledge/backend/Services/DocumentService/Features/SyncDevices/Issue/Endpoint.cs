using DocumentService.Domain;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices.Issue;

// FR-20, ADR-0037 決定 11: トークン発行（端末登録）。
// **平文のトークンはこの応答で 1 回だけ返る。** 保存されるのはハッシュのみである。
internal static class IssueSyncDeviceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (CreateSyncDeviceRequest req, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.DeviceName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["deviceName"] = ["端末名は必須です。"]
                });

            var now = DateTimeOffset.UtcNow;
            var (token, hash) = SyncTokens.Generate();
            var device = SyncDevice.Create(owner, req.DeviceName.Trim(), hash, now);
            db.SyncDevices.Add(device);
            await db.SaveChangesAsync(ct);
            // 監査: 資格情報の発行の記録（誰が・いつ）。トークン本体は記録しない。
            audit.Record("private-note.sync-token.issue", owner, "granted",
                $"device={device.Id}");
            return Results.Created($"/private-notes/devices/{device.Id}",
                new SyncTokenIssuedResponse(device.Id, device.DeviceName, token,
                    device.ExpiresAt));
        });
    }
}
