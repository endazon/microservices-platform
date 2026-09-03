using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.KnowledgeHealth.View;

// FR-10, FR-17, FR-18: 健全性指標の閲覧。**運用者・システム管理者のみ**（規則 2）。
// 権限が無い場合は 403 であり、**件数を含む一切の値を返さない**（403 の本文に部分結果を載せない）。
// 4 つの規則の全文は登録表（`KnowledgeHealthEndpoints`）にある。
internal static class KnowledgeHealthViewEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("", async (DashboardDbContext db, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var rows = await db.KnowledgeHealthObservations
                .Select(o => new { o.Indicator, o.DocScope, o.ObservedAt })
                .ToListAsync(ct);

            // 規則 3: 個人資料を除外する。**集合帰属で判定する**（KnowledgeDocScopes.IsPrivateNote）。
            var counted = rows.Where(r => !KnowledgeDocScopes.IsPrivateNote(r.DocScope)).ToList();

            var byIndicator = counted
                .GroupBy(r => r.Indicator, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(gr => gr.Key, gr => gr.Count(), StringComparer.OrdinalIgnoreCase);

            // planning#494 決定 3 (#1186): 現在のしきい値を件数へ併記する。
            // **観測値とは別の表から引く** —— 件数 0 の指標にも添える必要があるためである。
            var thresholds = await db.KnowledgeHealthIndicatorThresholds
                .ToDictionaryAsync(t => t.Indicator, t => t.ThresholdDays,
                    StringComparer.OrdinalIgnoreCase, ct);

            // 規則 4: 件数のみ。**7 指標すべてを 0 埋めして返す**（欠落と 0 を混同させない）。
            var indicators = KnowledgeHealthIndicators.All
                .Select(name => new KnowledgeHealthIndicatorDto(
                    name,
                    byIndicator.TryGetValue(name, out var count) ? count : 0,
                    thresholds.TryGetValue(name, out var days) ? days : null))
                .ToList();

            // 観測時刻は**除外前の全行**から採る —— 「いつの観測か」は集計対象の有無とは別の情報であり、
            // 個人資料しか無い期間に「観測が止まっている」と誤読させない。
            DateTimeOffset? observedAt = rows.Count == 0 ? null : rows.Max(r => r.ObservedAt);

            // 計画: 「閲覧は監査ログに記録する」。件数は残すが、対象の識別子は残さない。
            audit.Record("knowledge-health.read", http.User.Identity?.Name ?? "unknown", "granted",
                $"indicators={indicators.Count}");

            return Results.Ok(new KnowledgeHealthDto(observedAt, indicators));
        }).WithName("KnowledgeHealth").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<KnowledgeHealthDto>();
    }
}
