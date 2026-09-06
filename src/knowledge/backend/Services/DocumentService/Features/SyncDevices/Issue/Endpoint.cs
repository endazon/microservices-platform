using DocumentService.Domain;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncDevices.Issue;

// FR-20, ADR-0037 決定 11: トークン発行（端末登録）。
// **平文のトークンはこの応答で 1 回だけ返る。** 保存されるのはハッシュのみである。
internal static class IssueSyncDeviceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (CreateSyncDeviceRequest req,
            IValidator<CreateSyncDeviceRequest> validator, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner)
                return Results.Unauthorized();

            // FR-20, SC-20, ADR-0037 決定 11 / 計画 ADR-0030 §決定 / IADR-0371 決定 2 /
            // [[IADR-0398]] 決定 1: 端末名は必須。規則は `IssueSyncDeviceValidator` が持つ。
            // 🔴 **この呼び出しは 401 の後ろ**でなければならない（無資格の呼び出しに入力の形を教えない）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

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
