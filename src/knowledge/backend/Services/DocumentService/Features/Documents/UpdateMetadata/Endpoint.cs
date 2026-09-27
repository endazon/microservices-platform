using DocumentService.Common.Observability;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.UpdateMetadata;

// FR-06, UC-03: メタデータ（属性・タグ）のみ更新する。
// FR-06, UC-03, SC-05（#629）: メタデータ更新も**人については管理者限定**（計画の列挙「更新」「文書の編集」）。
// **BFF にこの口は無い**（実測。射程は「狭める」なので、ここで足さない）。
//
// FR-06, FR-08, 計画 ADR-0119 決定 2 (#1616): **機械クライアントは自分が `owner` の組織文書に限り**この口を使える
// （動的束縛。ロールは足さない）。そのため `AdminOnly` を口に積まず、同じ判定を口の中で行う
// （`DocumentManageScope.ForbidUnlessAdminOrMachine` → 取得 → `DenyUnlessAdminOrMachineOwnerAsync`）。
// `owner`・`doc_scope` はこの口で書き換えられない（主体を問わない）。
internal static class UpdateDocumentMetadataEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPatch("/{id:guid}/metadata", async (Guid id, UpdateMetadataRequest req,
            IValidator<UpdateMetadataRequest> validator,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, HttpContext http,
            DocumentReadAccess reads, UnitProjectMetrics unitProject, CancellationToken ct) =>
        {
            // FR-06, SC-05, ADR-0119 決定 2 (#1616): 入口の門。運用者だけの人は、`AdminOnly` を積んでいた頃と同じく
            // 文書の有無・入力の中身に依らず 403（入力検証より前）。管理者と機械クライアントだけが中へ進む。
            if (DocumentManageScope.ForbidUnlessAdminOrMachine(http.User) is { } metaForbidden)
                return metaForbidden;

            // FR-05, FR-19, UC-03, SC-05, ADR-0054, IADR-0047 / 計画 ADR-0030 §決定 /
            // IADR-0371 決定 2 / [[IADR-0398]] 決定 1: 入力検証（confidentiality → doc_scope の値域）。
            // メタデータ更新も属性を全置換するため機密区分を必須検証する。
            //
            // 🔴 **この位置（`FindAsync` より前）を動かしてはならない**（`Update` と同じ理由）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();

            // FR-06, ADR-0119 決定 2, ADR-0056 (#1616): 管理者でなければ、自分が owner の機械クライアントだけが書ける。
            // 他の主体の文書は、読めるなら 403・読めないなら 404。
            if (await DocumentManageScope.DenyUnlessAdminOrMachineOwnerAsync(http.User, doc, reads, db, ct)
                is { } metaDenied)
                return metaDenied;

            // FR-06, FR-19, ADR-0058 決定 2: doc_scope は作成時に確定し、以後変更できない。
            if (DocumentEndpoints.DocScopeChangedProblemOrNull(req.Attributes, doc.Attributes)
                is { } metaScopeFixed)
                return metaScopeFixed;

            // FR-06, FR-19, ADR-0119 決定 2, ADR-0036 §未確定事項 3 (#1616): owner も書き換えられない（主体を問わない）。
            if (DocumentManageScope.OwnerChangedProblemOrNull(req.Attributes, doc.Attributes)
                is { } metaOwnerFixed)
                return metaOwnerFixed;

            // FR-06, FR-16, AST/ADR-0032 決定 2, [[IADR-0405]] 決定 2 (#1233):
            // 制限 project の値は保存で外せない（`Update` と同じ規則・同じ位置）。
            if (DocumentEndpoints.RestrictedProjectDroppedProblemOrNull(req.Attributes, doc.Attributes)
                is { } metaProjectKept)
                return metaProjectKept;

            if (req.ExpectedVersion is { } expected && expected != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    expectedVersion = expected,
                    currentVersion = doc.Version
                });

            var (metaTagIds, metaUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (metaUnknown.Count > 0) return DocumentEndpoints.UnknownTagsProblem(metaUnknown);

            // FR-19, ADR-0061 決定 4, [[IADR-0455]] 決定 1 (#1471): **書き換える「前」に門の判定を取る**
            // （`Update` と同じ理由。［#1629］個人資料はここへ来ない —— 形を残す理由も `Update` と同じ）。
            var wasPublishable = DocumentEndpoints.PassesPublishGate(doc);
            doc.UpdateMetadata(DocumentBodyIntake.WithCurrentOwner(req.Attributes, doc.Attributes),
                metaTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            // FR-05, FR-16, SC-10, SC-12, ADR-0085 決定 4, [[IADR-0420]] (#1233):
            // メタデータ更新も属性を全置換する（`Update` と同じ理由・同じ位置）。
            unitProject.RecordIfUnitSubjectSavedWithoutProject(
                http.User, doc.Attributes, UnitProjectMetrics.OperationUpdateMetadata);
            var metaNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedIfIndexableOrWithdrawingAsync(
                bus, db, doc, wasPublishable, metaNames, ct);
            return Results.Ok(await DocumentEndpoints.ToDtoAsync(db, doc, metaNames, ct));
        });
    }
}
