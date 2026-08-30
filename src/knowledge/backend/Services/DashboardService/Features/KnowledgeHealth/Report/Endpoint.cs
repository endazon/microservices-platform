using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18: 観測値の受け口（指標 1 つ分のスナップショット置換）。
// 生産者は知識グラフ・文書カタログ側のサービスであり、**DB-per-service（ADR-0002）のため
// DashboardService から直接は数えられない**。集計主体を DashboardService に置く判断は
// IADR-0011（業務指標は DashboardService・技術/費用は可観測性スタック）に従う。
//
// ★［2026-08-29 移設 / #443］**`/dashboard/...` から `/internal/...` へ移し、認証を外した**
// （[[IADR-0299]] 決定 4。利用者裁定）。従前は `RequireAuthorization()`（認証済み）だったが、
// **生産者は利用者 JWT を持たない定期処理**であり、`client_credentials` の実装は
// リポジトリ本体に 1 行も無い。`/internal/notifications`（NotificationIngressEndpoints）が
// 同じ制約に対して採った形をそのまま倣う —— 内部 API は OpenAPI にも載せない。
//
// **統制**（定めたもの）: 第一防御は mesh の STRICT mTLS、多層防御としてネットワーク分離
// （内部サービスは host 非公開・Service は既定 ClusterIP・NetworkPolicy 既定拒否）。
// 🔴 **統制が働いていること（測れているもの）は別である。** 機械検査が実際に押さえているのは
// 「compose が host 公開しない」「Helm の Service に type: が現れない」の 2 点だけであり
// （NetworkIsolationTests）、**mTLS が実際に遮断していることは測れていない**。
// 残余リスク（同一ネットワーク内からは無認証で観測値を差し替えられる）は [[IADR-0299]] に
// 受容として記録した。作れるのは**指標名と不透明な鍵の集合だけ**であり、
// **受け口は書き込み専用で既存の観測値を読み出さない**（読み出しは閲覧の GET のみ・ロール限定）。
public static class ReportKnowledgeHealthEndpoint
{
    // 🔴 送信側 GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter.ObservationsPath と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。
    // `/internal/notifications` と同じく、**文字列の一致は両側のテストで固定している**。
    public const string ObservationsPath = "/internal/knowledge-health/observations";

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ObservationsPath, async (
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
        }).WithName("ReportKnowledgeHealth")
          .ExcludeFromDescription()
          .Produces(StatusCodes.Status202Accepted);
    }
}
