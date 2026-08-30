using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Update;

// FR-06, UC-03, SC-05（#629）: 編集は**管理者限定**（計画の列挙「文書の編集」）。
internal static class UpdateDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPut("/{id:guid}", async (Guid id, UpdateDocumentRequest req,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            // FR-05, UC-03, SC-05, IADR-0047: 更新でも機密区分を必須検証する（属性は全置換のため）。
            if (DocumentEndpoints.ConfidentialityProblemOrNull(req.Attributes) is { } updateError)
                return updateError;

            // FR-19, ADR-0054: doc_scope の値域検証（未知値は 400。欠落は拒否しない — 遡及付与しない方針）。
            if (DocumentEndpoints.DocScopeProblemOrNull(req.Attributes) is { } updateScopeError)
                return updateScopeError;

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // FR-06, FR-19, ADR-0058 決定 2: doc_scope は作成時に確定し、以後変更できない。
            if (DocumentEndpoints.DocScopeChangedProblemOrNull(req.Attributes, doc.Attributes)
                is { } updateScopeFixed)
                return updateScopeFixed;

            // FR-06, UC-03: 楽観的並行制御。期待版が現在版と異なれば lost update を防ぐため 409。
            if (req.ExpectedVersion is { } expected && expected != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    expectedVersion = expected,
                    currentVersion = doc.Version
                });

            var (updateTagIds, updateUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (updateUnknown.Count > 0) return DocumentEndpoints.UnknownTagsProblem(updateUnknown);

            doc.Update(req.Title, req.Attributes ?? [], updateTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            var updateNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedAsync(bus, doc, updateNames, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, updateNames));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
