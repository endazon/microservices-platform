using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Features.PrivateNotes;
// FR-20, #451-a: 端末・トークンの形は `Knowledge.Contracts/Dtos/PrivateNoteDto.cs` が持つ
// （BFF が同じ形を SC-20 の画面へ配るため、定義を 2 つ持たない）。
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices;

// FR-20, UC-11, SC-20, ADR-0037 決定 10〜13・15, [[IADR-0270]] 決定 3:
// 同期端末と同期トークンの管理（発行・再発行・一覧・個別失効・一括失効）。
//
// - **利用者が SC-20 から自ら発行・失効する。管理者承認を挟まない**（決定 10・11）。
// - **有効期限 30 日・更新は手動再発行のみ**（決定 12・15）。自動リフレッシュの端点は存在しない。
// - **トークンの平文は発行・再発行の応答で 1 回だけ**返す。以後どの応答にも現れない。
// - 一覧・失効の対象は本人の端末のみ。他人の端末は存在ごと秘匿する（404）。
public static class SyncDeviceEndpoints
{
    public static IEndpointRouteBuilder MapSyncDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/private-notes/devices").WithTags("PrivateNotes")
            .RequireAuthorization();

        // SC-20: 端末一覧（トークンのハッシュ・平文はどちらも出さない）。
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var devices = await db.SyncDevices.Where(d => d.OwnerId == owner)
                .OrderBy(d => d.IssuedAt).ToListAsync(ct);
            return Results.Ok(devices.Select(d => ToDto(d, now)).ToList());
        });

        // FR-20, ADR-0037 決定 11: トークン発行（端末登録）。
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

        // ADR-0037 決定 15: **手動再発行**。旧トークンは即時に無効化される。
        // 期限切れ・失効済みの端末に対しても本人操作として再発行できる（回復経路はこれだけである）。
        g.MapPost("/{id:guid}/reissue", async (Guid id, HttpContext http, DocumentDbContext db,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var device = await FindOwnedAsync(db, owner, id, ct);
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

        // ADR-0037 決定 13: 個別失効。
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();
            var device = await FindOwnedAsync(db, owner, id, ct);
            if (device is null) return Results.NotFound();

            device.Revoke(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync-token.revoke", owner, "granted",
                $"device={device.Id}");
            return Results.NoContent();
        });

        // ADR-0037 決定 13: **全端末の一括失効**（端末紛失時の防御。どの端末か特定できない場面用）。
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

        return app;
    }

    private static async Task<SyncDevice?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var device = await db.SyncDevices.FindAsync([id], ct);
        return device is not null && device.OwnerId == owner ? device : null;
    }

    private static SyncDeviceDto ToDto(SyncDevice d, DateTimeOffset now) => new(
        d.Id, d.DeviceName, d.IssuedAt, d.ExpiresAt, d.RevokedAt is not null, d.LastSyncAt,
        d.IsActive(now));
}

// FR-20, #451-a: `CreateSyncDeviceRequest` / `SyncTokenIssuedResponse`（平文トークンは本応答で
// 1 回だけ返る。保存されるのはハッシュのみ）/ `SyncDeviceDto` / `RevokeAllSyncDevicesResponse` は
// `Knowledge.Contracts.Dtos` にある。**BFF が同じ形を配るため、定義はそちら 1 つだけである。**
