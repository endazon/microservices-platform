using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.BreakGlass.Activate;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): **緊急アクセスの発動**（所有者本人だけ）。
//
// 応答:
//   201 … 発動した（発動記録の写し）。発動の監査ログ 1 行を同じ保存で書く。
//   400 … 理由が空・長すぎる／指名先が空・本人・機械・`platform-admin` でない
//   404 … 他人の資料・無い・個人資料でない・主体が機械（存在ごと秘匿）
//   409 … 論理削除中の資料／同じ資料に有効な発動が既にある（1 件に同時に 1 つ）
//   503 … 指名先が管理者かを利用者名簿で確かめられない（fail-closed。発動しない）
internal static class ActivateBreakGlassEndpoint
{
    internal const int NomineeMaxLength = 200;

    internal static void Map(RouteGroupBuilder owner)
    {
        owner.MapPost("/", async (Guid id, ActivateBreakGlassRequest req, HttpContext http, DocumentDbContext db,
            IApproverRoleDirectory roles, TimeProvider clock, IAuditLogger audit, CancellationToken ct) =>
        {
            if (BreakGlassEndpoints.HumanSubjectOf(http) is not { } ownerId) return Results.NotFound();
            if (await BreakGlassEndpoints.FindOwnedNoteAsync(db, ownerId, id, ct) is not { } owned)
                return Results.NotFound();

            var errors = Validate(req, ownerId);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var reason = req.Reason!.Trim();
            var nominee = req.NomineeUserId!.Trim();

            if (owned.Note.IsDeleted)
                return Results.Problem(title: "論理削除中の資料には緊急アクセスを発動できません。",
                    statusCode: StatusCodes.Status409Conflict);

            var now = clock.GetUtcNow();
            if (await BreakGlassEndpoints.FindActiveAsync(db, id, now, ct) is not null)
                return Results.Problem(title: "この資料には有効な緊急アクセスが既にあります。終了してから発動してください。",
                    statusCode: StatusCodes.Status409Conflict);

            switch (await roles.GetAdminStateAsync(nominee, ct))
            {
                case ApproverAdminState.Admin:
                    break;
                case ApproverAdminState.NotAdmin:
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["nomineeUserId"] = ["指名できるのはシステム管理者（platform-admin）だけです。"],
                    });
                default:
                    // 🔴 分からないを「管理者」へ畳まない（発動しない）。
                    return Results.Problem(title: "指名先がシステム管理者かを確認できません。時間をおいて再度お試しください。",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var grant = BreakGlassGrant.Activate(id, ownerId, nominee, reason, now);
            db.BreakGlassGrants.Add(grant);
            db.BreakGlassAuditEntries.Add(BreakGlassAuditEntry.Activated(grant));
            await db.SaveChangesAsync(ct);
            // 構造化ログへの 2 つ目の出口。理由（利用者の入力）は載せない（NFR-28）。
            audit.Record("break-glass.activate", ownerId, "granted",
                $"documentId={id} grantId={grant.Id} nominee={nominee} expiresAt={grant.ExpiresAt:O}");

            return Results.Created($"/private-notes/{id}/break-glass", grant.ToDto(now));
        });
    }

    // 理由は必須（空白だけは不可）。指名先は 1 人の利用者名で、本人・機械（サービスアカウント）を指名できない。
    internal static Dictionary<string, string[]> Validate(ActivateBreakGlassRequest req, string ownerId)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            errors["reason"] = ["理由を入力してください。"];
        else if (reason.Length > BreakGlassPolicy.ReasonMaxLength)
            errors["reason"] = [$"理由は {BreakGlassPolicy.ReasonMaxLength} 文字以内で入力してください。"];

        var nominee = req.NomineeUserId?.Trim();
        if (string.IsNullOrEmpty(nominee))
            errors["nomineeUserId"] = ["閲覧を許すシステム管理者を 1 人指名してください。"];
        else if (nominee.Length > NomineeMaxLength)
            errors["nomineeUserId"] = [$"指名先は {NomineeMaxLength} 文字以内で指定してください。"];
        else if (string.Equals(nominee, ownerId, StringComparison.Ordinal))
            errors["nomineeUserId"] = ["本人は指名できません。"];
        else if (nominee.StartsWith(MachinePrincipal.ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase))
            errors["nomineeUserId"] = ["サービスアカウントは指名できません。"];

        return errors;
    }
}
