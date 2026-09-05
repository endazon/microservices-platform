using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Update;

// FR-06, UC-03, SC-05（#629）: 編集は**管理者限定**（計画の列挙「文書の編集」）。
internal static class UpdateDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPut("/{id:guid}", async (Guid id, UpdateDocumentRequest req,
            IValidator<UpdateDocumentRequest> validator,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            // FR-05, FR-06, FR-19, UC-03, SC-05 / 計画 ADR-0030 §決定 / IADR-0371 決定 2 /
            // [[IADR-0398]] 決定 1: 入力検証（title → confidentiality → doc_scope の値域）。
            // 規則は `UpdateDocumentValidator` が持ち、**先頭 1 件をその鍵で返す**
            // （移送前は最初のガード節で返っていた。宣言順が応答の契約である）。
            //
            // 🔴 **この位置（`FindAsync` より前）を動かしてはならない。** 移送前も 3 本とも取得の
            // 前に居た —— **不存在の文書 ID への空題名更新は 400 であり 404 ではない。**
            // `IValidator<T>` が引数にあることは順序の証拠にならない（引数は解決であって実行ではない。
            // IADR-0395 決定 2）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

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
            await DocumentEndpoints.PublishUpdatedAsync(bus, db, doc, updateNames, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, updateNames));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
