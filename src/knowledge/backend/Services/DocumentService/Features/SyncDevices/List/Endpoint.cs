using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncDevices.List;

// SC-20: 端末一覧（トークンのハッシュ・平文はどちらも出さない）。
internal static class ListSyncDevicesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var devices = await db.SyncDevices.Where(d => d.OwnerId == owner)
                .OrderBy(d => d.IssuedAt).ToListAsync(ct);
            // 🔴 **時計はここで畳む**（IADR-0406 決定 4）。`Active` は導出であり、
            // 生成マッパへ渡すのは導出**済み**の bool である。
            return Results.Ok(devices.Select(d => SyncDeviceMapper.ToDto(d, d.IsActive(now))).ToList());
        });
    }
}
