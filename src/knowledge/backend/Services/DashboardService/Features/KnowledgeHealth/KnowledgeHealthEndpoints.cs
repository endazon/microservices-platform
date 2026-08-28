using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.KnowledgeHealth;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006 (#443): ナレッジ健全性の指標。
//
// 計画 `06_technical/05_observability-ops.md` §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）が
// **同時に満たすべき 4 つの規則**を定めている。**本節は ABAC の文書単位判定に対する明示的な例外**であり、
// 🔴 **3 条件（件数のみ・ロール限定・個人資料除外）のうち 1 つでも欠けると存在秘匿が崩れる。個別に緩めない。**
//
//  1. **集計範囲は全体**（閲覧者の権限で絞らない）。運用者ごとに数字が変わると、指標が改善したのか
//     担当者が変わっただけなのかを判別できず、時系列の比較が成り立たない。
//  2. **閲覧は運用者・システム管理者に限定**。全体集計を許す以上、**閲覧側のロール制限が唯一の統制点**である。
//  3. **個人資料（`private-note`）は集計から除外**。所有者本人が閲覧する場合も含め**一律**である
//     （例外を設けると集計値がロールごとに変わり、1 の前提が崩れる）。除外は
//     **件数の変動から個人資料の存在・増減が推測される経路を塞ぐ**意味も持つ（ADR-0034 決定 2 と同じ理由）。
//  4. **個々の文書名を出さず件数のみ**。ドリルダウンの導線を設けない。
//
// **画面（SC-10 のナレッジ健全性節）は本 issue の射程外**である（引き受け先は #452 / #504）。
// ここで用意するのは集計と統制であり、表示ではない。
public static class KnowledgeHealthEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dashboard/knowledge-health").WithTags("KnowledgeHealth");

        // FR-10, FR-17, FR-18: 健全性指標の閲覧。**運用者・システム管理者のみ**（規則 2）。
        // 権限が無い場合は 403 であり、**件数を含む一切の値を返さない**（403 の本文に部分結果を載せない）。
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

            // 規則 4: 件数のみ。**7 指標すべてを 0 埋めして返す**（欠落と 0 を混同させない）。
            var indicators = KnowledgeHealthIndicators.All
                .Select(name => new KnowledgeHealthIndicatorDto(
                    name, byIndicator.TryGetValue(name, out var count) ? count : 0))
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

        // FR-10, FR-17, FR-18: 観測値の受け口（指標 1 つ分のスナップショット置換）。
        // 生産者は知識グラフ・文書カタログ側のサービスであり、**DB-per-service（ADR-0002）のため
        // DashboardService から直接は数えられない**。集計主体を DashboardService に置く判断は
        // IADR-0011（業務指標は DashboardService・技術/費用は可観測性スタック）に従う。
        //
        // 認可は**認証済み**とする（`POST /dashboard/events` と同じ）。書き込むのはサービスであり、
        // 管理系ロールを要求すると計測経路がロール設計に縛られる。**閲覧側の統制は上の GET が持つ。**
        g.MapPost("/observations", async (
            KnowledgeHealthReportRequest req, DashboardDbContext db, CancellationToken ct) =>
        {
            if (!KnowledgeHealthIndicators.IsValid(req.Indicator))
                return Results.BadRequest(new
                {
                    error = "indicator must be one of: " + string.Join(", ", KnowledgeHealthIndicators.All)
                });

            var indicator = KnowledgeHealthIndicators.Normalize(req.Indicator);
            var observedAt = DateTimeOffset.UtcNow;

            // スナップショット置換: 当該指標の既存行を落としてから差し替える。
            var stale = await db.KnowledgeHealthObservations
                .Where(o => o.Indicator == indicator)
                .ToListAsync(ct);
            db.KnowledgeHealthObservations.RemoveRange(stale);

            var observations = (req.Observations ?? [])
                .Where(o => !string.IsNullOrWhiteSpace(o.SubjectKey))
                .Select(o => KnowledgeHealthObservation.Create(indicator, o.SubjectKey, o.DocScope, observedAt))
                .ToList();
            db.KnowledgeHealthObservations.AddRange(observations);
            await db.SaveChangesAsync(ct);

            // **受け付けた件数だけを返す**（個人資料を含み得る生の値であり、ここは集計面ではない）。
            return Results.Accepted(value: new { indicator, accepted = observations.Count });
        }).WithName("ReportKnowledgeHealth").RequireAuthorization()
          .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}
