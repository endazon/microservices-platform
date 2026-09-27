using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
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
        g.MapPost("/", async (Guid id, CreateShareRequest req,
            IValidator<CreateShareRequest> validator, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, HttpContext http, CancellationToken ct) =>
        {
            // FR-20, ADR-0036 D-06 / 計画 ADR-0030 §決定 / IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9:
            // 共有先の入力検証。規則は `GrantDocumentShareValidator` が持つ。
            //
            // 🔴 **述語は 1 本のまま**（`subjectId` の空 ∨ `subjectType` の不正 → **1 件**）。
            // 🔴 **鍵は `errors`**（メッセージが 2 項目にまたがるため、片方の名前を鍵にできない）。
            // 🔴 **この位置（取得・認可より前）を動かしてはならない** —— 移送前もそうだった。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // 所有者限定（再共有不可の実体）。認可を検証より先に見る——他人の文書に対する
            // 存在・重複の情報を返さない（拒否は 404。ADR-0056 決定 1）。
            // ［2026-09-28 / #1679］主体は作成の口と同じ関数から引く（`DocumentManageScope.OwnerSubject`。本文の投入と同じ）。
            var subject = DocumentManageScope.OwnerSubject(http.User);
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, subject))
                return Results.NotFound();

            var exists = await db.DocumentShares.AnyAsync(s => s.DocumentId == id
                && s.SubjectType == req.SubjectType && s.SubjectId == req.SubjectId);
            if (exists)
                return Results.Conflict(new { message = "既に共有済みです。" });

            // 付与者も同じ主体名で記録する（`CanWrite` が真なので非 null。腕 B の機械で `Identity.Name` の null を入れない）。
            var share = DocumentShare.Create(id, req.SubjectType, req.SubjectId, subject!);
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
