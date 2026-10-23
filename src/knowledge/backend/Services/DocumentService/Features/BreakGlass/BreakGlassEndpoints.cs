using DocumentService.Domain;
using DocumentService.Features.BreakGlass.Activate;
using DocumentService.Features.BreakGlass.End;
using DocumentService.Features.BreakGlass.GetStatus;
using DocumentService.Features.BreakGlass.ListAudit;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.BreakGlass;

// FR-19, UC-11, SC-19, SC-03, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// **緊急アクセス（Break-glass）の端点の合成点**（planning#841 の裁定 2026-10-23 による本人同意型の管理者の閲覧）。
//
//   | 口                                              | 誰が                     |
//   | POST /private-notes/{id}/break-glass            | 所有者本人だけ（発動）     |
//   | POST /private-notes/{id}/break-glass/end        | 所有者本人だけ（早期終了） |
//   | GET  /private-notes/{id}/break-glass            | 所有者本人だけ（状態）     |
//   | GET  /break-glass/audit                         | platform-admin（全件）と本人（自分の資料の行だけ） |
//
// 🔴 **管理者を含む第三者の発動経路を作らない**（D-08）。所有者の判定は個人資料の台帳（`PrivateNote.OwnerId`）で行い、
//   他人の資料は存在ごと秘匿する（404。他の `/private-notes/*` と同じ作法）。ロールでは開かない。
// 🔴 **主体はトークンからしか採らない**（本文・クエリに主体の口を作らない）。機械の主体は発動も終了もできない。
// 🔴 閲覧そのものの許可はここに無い —— `BreakGlassViewGate`（SC-03 の閲覧の 3 口だけが呼ぶ）にある。
public static class BreakGlassEndpoints
{
    public static IEndpointRouteBuilder MapBreakGlassEndpoints(this IEndpointRouteBuilder app)
    {
        // 所有者本人の操作。認証は必須・ロールは要求しない（所有者が管理者でなくても発動できる）。
        var owner = app.MapGroup("/private-notes/{id:guid}/break-glass").WithTags("BreakGlass").RequireAuthorization();
        ActivateBreakGlassEndpoint.Map(owner);
        EndBreakGlassEndpoint.Map(owner);
        GetBreakGlassStatusEndpoint.Map(owner);

        // 監査ログの読み取り。認証は必須。誰の行まで読めるかは口の中で決める（管理者は全件、他は本人の行だけ）。
        var audit = app.MapGroup("/break-glass").WithTags("BreakGlass").RequireAuthorization();
        ListBreakGlassAuditEndpoint.Map(audit);

        return app;
    }

    // 人の主体の利用者名。機械・名前の無い主体は null（発動・終了・状態の口は 404 にする）。
    internal static string? HumanSubjectOf(HttpContext http)
        => MachinePrincipal.IsMachine(http.User) ? null : PrivateNoteEndpoints.SubjectOf(http);

    // 所有者本人の個人資料（台帳と文書の両方で本人のもの）。他人の・無い・個人資料でないは null（＝ 404）。
    internal static async Task<(PrivateNote Note, Document Doc)?> FindOwnedNoteAsync(
        DocumentDbContext db, string owner, Guid id, CancellationToken ct)
    {
        var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
        if (note is null) return null;

        var doc = await db.Documents.FindAsync([id], ct);
        if (doc is null || !DocumentScopes.IsPrivateNote(doc.Attributes)
            || !DocumentBodyIntake.IsOwnedBy(doc.Attributes, owner))
            return null;
        return (note, doc);
    }

    // 資料 1 件の、いま有効な発動記録（無ければ null）。有効の判定はドメインの 1 関数（`IsActiveAt`）で行う。
    internal static async Task<BreakGlassGrant?> FindActiveAsync(
        DocumentDbContext db, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.BreakGlassGrants
            .Where(g => g.DocumentId == documentId && g.ActivatedAt <= now && g.ExpiresAt > now)
            .ToListAsync(ct);
        return candidates.Where(g => g.IsActiveAt(now)).OrderByDescending(g => g.ActivatedAt).FirstOrDefault();
    }

    internal static BreakGlassStatusResponse StatusOf(BreakGlassGrant? active, DateTimeOffset now)
        => new(active?.ToDto(now));
}
