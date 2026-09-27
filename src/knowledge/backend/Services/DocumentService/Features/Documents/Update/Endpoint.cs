using DocumentService.Common.Observability;
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
            DocumentDbContext db, IDocumentUpdatedPublisher bus, HttpContext http,
            UnitProjectMetrics unitProject, CancellationToken ct) =>
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

            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();

            // FR-06, FR-19, ADR-0058 決定 2: doc_scope は作成時に確定し、以後変更できない。
            if (DocumentEndpoints.DocScopeChangedProblemOrNull(req.Attributes, doc.Attributes)
                is { } updateScopeFixed)
                return updateScopeFixed;

            // FR-06, FR-16, AST/ADR-0032 決定 2, [[IADR-0405]] 決定 2 (#1233):
            // 制限 project の値は保存で外せない（属性は全置換であり、落とすと後段の除外が効かなくなる）。
            if (DocumentEndpoints.RestrictedProjectDroppedProblemOrNull(req.Attributes, doc.Attributes)
                is { } updateProjectKept)
                return updateProjectKept;

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

            // FR-19, ADR-0061 決定 4, [[IADR-0455]] 決定 1 (#1471): **書き換える「前」に門の判定を取る。**
            // 属性は全置換なので、個人資料の露出キーを落とす・`excluded` にする保存は ON → OFF の撤収になる。
            // ［#1629］個人資料は上の `FindManageableAsync` で 404 になりここへ来ない。組織文書では撤収の形と単純な門は
            // 同値である（`PassesPublishGate` は組織文書に常に真）ため、形は残す（[[IADR-0455]] の 2026-09-27 追記）。
            var wasPublishable = DocumentEndpoints.PassesPublishGate(doc);
            doc.Update(req.Title, req.Attributes ?? [], updateTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            // FR-05, FR-16, SC-10, SC-12, ADR-0085 決定 4, [[IADR-0420]] (#1233):
            // **編集も保存である。** 属性は全置換なので、`project` を落とした保存もここで数える
            // （落とせないのは**制限**プロジェクトの値だけ。上の統制と母集合が違う）。
            unitProject.RecordIfUnitSubjectSavedWithoutProject(
                http.User, doc.Attributes, UnitProjectMetrics.OperationUpdate);
            var updateNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedIfIndexableOrWithdrawingAsync(
                bus, db, doc, wasPublishable, updateNames, ct);
            return Results.Ok(await DocumentEndpoints.ToDtoAsync(db, doc, updateNames, ct));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
