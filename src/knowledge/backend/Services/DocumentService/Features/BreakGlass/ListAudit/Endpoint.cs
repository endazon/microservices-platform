using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.BreakGlass.ListAudit;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): **緊急アクセスの監査ログの読み取り**
// （planning#841 の裁定 6: 読めるのは `platform-admin` と本人。本人は自分の資料の分だけ）。
//
//   - `platform-admin`（トークンの realm ロール）… 全件。`documentId` で 1 件の資料に絞れる。
//   - それ以外の人 … **発動者（所有者）が本人の行だけ**。`documentId` は本人の行の中でさらに絞るだけ（他人の行は 0 件）。
//   - 機械の主体 … 403（管理者ロールを持つサービスアカウントにも開かない。読むのは人である）。
//
// 新しい順（日時の降順・同時刻は ID の降順）に `limit` 件（既定 50・上限 200。範囲外は 400 —— 黙って丸めない）。
// 🔴 **本文・題名は返らない**（表に列が無い。`BreakGlassAuditEntry` の注記）。
internal static class ListBreakGlassAuditEndpoint
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/audit", async (Guid? documentId, int? limit, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (MachinePrincipal.IsMachine(http.User)) return Results.Forbid();
            if (DocumentService.Features.PrivateNotes.PrivateNoteEndpoints.SubjectOf(http) is not { } subject)
                return Results.Unauthorized();

            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = [$"limit は 1〜{MaxLimit} の範囲で指定してください。"],
                });

            var isAdmin = http.User.IsInRole(PlatformAuthPolicies.AdminRole);
            var q = db.BreakGlassAuditEntries.AsNoTracking();
            if (!isAdmin)
                q = q.Where(a => a.OwnerId == subject);
            if (documentId is { } doc)
                q = q.Where(a => a.DocumentId == doc);

            var entries = await q
                .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
                .Take(take)
                .ToListAsync(ct);

            var grantIds = entries.Select(e => e.GrantId).Distinct().ToList();
            var grants = await db.BreakGlassGrants.AsNoTracking()
                .Where(gr => grantIds.Contains(gr.Id))
                .ToDictionaryAsync(gr => gr.Id, ct);

            return Results.Ok(entries.Select(e => e.ToDto(grants.GetValueOrDefault(e.GrantId))).ToList());
        });
    }
}
