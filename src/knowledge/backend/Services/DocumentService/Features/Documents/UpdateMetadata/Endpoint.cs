using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.UpdateMetadata;

// FR-06, UC-03: メタデータ（属性・タグ）のみ更新する。
// FR-06, UC-03, SC-05（#629）: メタデータ更新も**管理者限定**（計画の列挙「更新」「文書の編集」）。
// **BFF にこの口は無い**（実測。射程は「狭める」なので、ここで足さない）。
internal static class UpdateDocumentMetadataEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPatch("/{id:guid}/metadata", async (Guid id, UpdateMetadataRequest req,
            IValidator<UpdateMetadataRequest> validator,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            // FR-05, FR-19, UC-03, SC-05, ADR-0054, IADR-0047 / 計画 ADR-0030 §決定 /
            // IADR-0371 決定 2 / [[IADR-0398]] 決定 1: 入力検証（confidentiality → doc_scope の値域）。
            // メタデータ更新も属性を全置換するため機密区分を必須検証する。
            //
            // 🔴 **この位置（`FindAsync` より前）を動かしてはならない**（`Update` と同じ理由）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // FR-06, FR-19, ADR-0058 決定 2: doc_scope は作成時に確定し、以後変更できない。
            if (DocumentEndpoints.DocScopeChangedProblemOrNull(req.Attributes, doc.Attributes)
                is { } metaScopeFixed)
                return metaScopeFixed;

            if (req.ExpectedVersion is { } expected && expected != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    expectedVersion = expected,
                    currentVersion = doc.Version
                });

            var (metaTagIds, metaUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (metaUnknown.Count > 0) return DocumentEndpoints.UnknownTagsProblem(metaUnknown);

            doc.UpdateMetadata(req.Attributes ?? [], metaTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            var metaNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedAsync(bus, db, doc, metaNames, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, metaNames));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
