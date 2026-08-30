using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.GrantShare;

// FR-20, ADR-0036 D-06: 共有の付与（所有者のみ。個人／グループの 2 種別）。
internal static class GrantDocumentShareEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (Guid id, CreateShareRequest req, DocumentDbContext db,
            HttpContext http) =>
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
            await db.SaveChangesAsync();
            return Results.Created($"/documents/{id}/shares",
                new DocumentShareDto(share.SubjectType, share.SubjectId, share.GrantedBy,
                    share.CreatedAt));
        });
    }
}
