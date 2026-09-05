using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.GrantShare;

// FR-20, ADR-0036 D-06, ADR-0061 決定 5: 共有の付与（所有者のみ。個人／グループの 2 種別）。
//
// **［#1184］付与のあと `DocumentUpdated` を再発行する**（[[IADR-0396]] 決定 3）——
// 索引のペイロードが運ぶ `shared_with` は**発行時点の写し**であり、再発行しないと
// **共有した相手に永久に見えない**（共有先ベースの分岐が索引の側で成立しない）。
// 門は他の書き込み経路と同じ `PublishUpdatedIfIndexableAsync`（露出 OFF の個人資料は出さない）。
internal static class GrantDocumentShareEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (Guid id, CreateShareRequest req, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SubjectId)
                || !ShareSubjectType.IsValid(req.SubjectType))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["errors"] =
                    [
                        $"subjectType は {string.Join(" / ", ShareSubjectType.All)} のいずれか、"
                        + "subjectId は非空である必要があります。"
                    ]
                });

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // 所有者限定（再共有不可の実体）。認可を検証より先に見る——他人の文書に対する
            // 存在・重複の情報を返さない（拒否は 404。ADR-0056 決定 1）。
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var exists = await db.DocumentShares.AnyAsync(s => s.DocumentId == id
                && s.SubjectType == req.SubjectType && s.SubjectId == req.SubjectId);
            if (exists)
                return Results.Conflict(new { message = "既に共有済みです。" });

            var share = DocumentShare.Create(id, req.SubjectType, req.SubjectId,
                http.User.Identity!.Name!);
            db.DocumentShares.Add(share);
            await db.SaveChangesAsync(ct);
            await DocumentEndpoints.PublishUpdatedIfIndexableAsync(
                bus, db, doc, await TagResolver.NamesAsync(db, ct), ct);
            return Results.Created($"/documents/{id}/shares",
                new DocumentShareDto(share.SubjectType, share.SubjectId, share.GrantedBy,
                    share.CreatedAt));
        });
    }
}
