using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.RevokeShare;

// FR-20, ADR-0036 D-06, ADR-0061 決定 5: 共有の取り消し（所有者のみ。取り消し可——ただし
// 「既に見られた事実」は取り消せない）。
//
// 🔴 **［#1184］取り消しのあと `DocumentUpdated` を再発行する**（[[IADR-0396]] 決定 3）。
// 付与側より重要である —— 再発行しないと**取り消した相手に索引の側から見え続ける**
// （厳格化の遅延＝漏れる向き）。
internal static class RevokeDocumentShareEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{subjectType}/{subjectId}", async (Guid id, string subjectType,
            string subjectId, DocumentDbContext db, IDocumentUpdatedPublisher bus,
            HttpContext http, CancellationToken ct) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            // ［2026-09-28 / #1679］主体は作成の口と同じ関数から引く（`DocumentManageScope.OwnerSubject`）。
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, DocumentManageScope.OwnerSubject(http.User)))
                return Results.NotFound();

            var share = await db.DocumentShares.FirstOrDefaultAsync(s => s.DocumentId == id
                && s.SubjectType == subjectType && s.SubjectId == subjectId);
            if (share is null) return Results.NotFound();

            db.DocumentShares.Remove(share);
            // FR-06, ADR-0027 §決定, IADR-0608 決定 1 (#2168): 保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
            // 🔴 共有先（`sharedWith`）は**保存の後の**共有台帳から解決する（今回の付与・取り消しが載る）。保存の前に積む形にしない。
            await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(bus, db, doc, ct);
            return Results.NoContent();
        });
    }
}
