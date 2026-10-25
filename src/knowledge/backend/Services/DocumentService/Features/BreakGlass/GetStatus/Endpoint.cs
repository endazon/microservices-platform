using DocumentService.Infrastructure.Persistence;

namespace DocumentService.Features.BreakGlass.GetStatus;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): **資料 1 件の緊急アクセスの状態**（所有者本人だけ）。
// 有効な発動があればその写し（残り時間の表示用）、無ければ `active=null`。
// 🔴 この値を閲覧の許可の根拠にしない（許可は `BreakGlassViewGate` が要求ごとに発動記録で決める）。
//
// 応答: 200 … 状態／404 … 他人の資料・無い・主体が機械
internal static class GetBreakGlassStatusEndpoint
{
    internal static void Map(RouteGroupBuilder owner)
    {
        owner.MapGet("/", async (Guid id, HttpContext http, DocumentDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (BreakGlassEndpoints.HumanSubjectOf(http) is not { } ownerId) return Results.NotFound();
            if (await BreakGlassEndpoints.FindOwnedNoteAsync(db, ownerId, id, ct) is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            var active = await BreakGlassEndpoints.FindActiveAsync(db, id, now, ct);
            if (active is not null && !string.Equals(active.OwnerId, ownerId, StringComparison.Ordinal))
                active = null;
            return Results.Ok(BreakGlassEndpoints.StatusOf(active, now));
        });
    }
}
