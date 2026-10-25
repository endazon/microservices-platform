using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.BreakGlass.End;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): **本人による早期終了**（planning#841 の裁定 4）。
// 終了日時は「本人の終了か、発動＋15 分の早いほう」になる。終了の監査ログ 1 行を同じ保存で書く。
//
// 応答: 200 … 終えた（発動記録の写し。`active=false`）／404 … 他人の資料・無い・有効な発動が無い・主体が機械
internal static class EndBreakGlassEndpoint
{
    internal static void Map(RouteGroupBuilder owner)
    {
        owner.MapPost("/end", async (Guid id, HttpContext http, DocumentDbContext db, TimeProvider clock,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (BreakGlassEndpoints.HumanSubjectOf(http) is not { } ownerId) return Results.NotFound();
            // 論理削除中でも終えられる（終える向きは常に許す）。
            if (await BreakGlassEndpoints.FindOwnedNoteAsync(db, ownerId, id, ct) is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            var grant = await BreakGlassEndpoints.FindActiveAsync(db, id, now, ct);
            // 発動者が本人でない記録（所有者が変わる前の記録）は本人の操作として終えない。
            if (grant is null || !string.Equals(grant.OwnerId, ownerId, StringComparison.Ordinal) || !grant.EndByOwner(now))
                return Results.NotFound();

            db.BreakGlassAuditEntries.Add(BreakGlassAuditEntry.Ended(grant, now));
            await db.SaveChangesAsync(ct);
            audit.Record("break-glass.end", ownerId, "granted", $"documentId={id} grantId={grant.Id}");

            return Results.Ok(grant.ToDto(now));
        });
    }
}
