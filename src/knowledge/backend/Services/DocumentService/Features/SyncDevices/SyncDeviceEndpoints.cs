using DocumentService.Domain;
using DocumentService.Features.SyncDevices.Issue;
using DocumentService.Features.SyncDevices.List;
using DocumentService.Features.SyncDevices.Reissue;
using DocumentService.Features.SyncDevices.Revoke;
using DocumentService.Features.SyncDevices.RevokeAll;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncDevices;

// FR-20, UC-11, SC-20, ADR-0037 決定 10〜13・15, [[IADR-0270]] 決定 3:
// 同期端末と同期トークンの管理（発行・再発行・一覧・個別失効・一括失効）の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/SyncDevices/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group、所有者スコープの照会。
//
// **［#1279］DTO 変換はここに無い。** `Features/SyncDevices/List/SyncDeviceMapper.cs` が持つ
// 生成マッパへ移した（計画 ADR-0030 §決定 / IADR-0406）。⚠ **置き場が 3 段目へ動いている** ——
// 写像を使うのは一覧の 1 操作だけであり、`ADR-0068` 決定 2（1 操作しか使わないファイルは 3 段目）の
// 適用結果である。**この登録表は 5 操作すべてが使うので 2 段目のままである。**
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

        ListSyncDevicesEndpoint.Map(g);
        IssueSyncDeviceEndpoint.Map(g);
        ReissueSyncDeviceEndpoint.Map(g);
        RevokeSyncDeviceEndpoint.Map(g);
        RevokeAllSyncDevicesEndpoint.Map(g);

        return app;
    }

    internal static async Task<SyncDevice?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var device = await db.SyncDevices.FindAsync([id], ct);
        return device is not null && device.OwnerId == owner ? device : null;
    }
}
