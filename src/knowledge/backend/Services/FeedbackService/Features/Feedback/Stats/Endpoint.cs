using FeedbackService.Domain;
using FeedbackService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace FeedbackService.Features.Feedback.Stats;

// FR-08: 集計（👍/👎 件数・満足率）。品質可視化（FR-10 ダッシュボード）の入力。
// **統計の参照は運用者・管理者に限る**（計画 FR-08 / SC-10・2026-08-07 確定）。
// ［2026-08-10 是正 / #521・IADR-0158］従前は「集計値のみで PII を含まないため AdminOnly は
//   課さない」としていたが、**その根拠は計画側で失効していた**。判断軸は PII の有無ではなく
//   **権限で絞るか**である（裁定依頼 planning#236 案 2）。閲覧ロールは SC-10 の画面
//   （#544 で運用者へ開いた）と同じ線で引く。BFF 側にも同じ認可がある（IADR-0044 多層防御）。
//   days — FR-10: 期間指定（日数）。指定時はその範囲に絞る。未指定は全期間（後方互換）。
internal static class FeedbackStatsEndpoint
{
    // FR-10: 満足率の集計期間の上限（DashboardService の利用状況集計と揃える）。
    // **統計だけが使う**ため 3 段目に置く（ADR-0068 決定 2）。
    private const int MaxStatsDays = 90;

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/stats", async (Guid? answerId, int? days, FeedbackDbContext db, CancellationToken ct) =>
        {
            var q = db.Feedback.AsQueryable();
            if (answerId is { } aid && aid != Guid.Empty)
                q = q.Where(f => f.AnswerId == aid);
            // FR-10: 期間指定があれば絞り込む。ダッシュボードの利用状況と満足率の期間を揃えるため、
            // BFF は利用状況と同じ days を渡す（DashboardService と同一のクランプ・起点算出）。
            if (days is { } d)
            {
                var since = SinceUtc(d);
                q = q.Where(f => f.CreatedAt >= since);
            }

            var up = await q.CountAsync(f => f.Rating == FeedbackRating.Up, ct);
            var down = await q.CountAsync(f => f.Rating == FeedbackRating.Down, ct);
            var total = up + down;
            var rate = total == 0 ? 0d : (double)up / total;
            return Results.Ok(new FeedbackStatsDto(up, down, total, rate));
        }).WithName("FeedbackStats")
          // FR-08, SC-10（計画確定 2026-08-07・`05_screens:431`）: **統計は運用者・管理者に限る。**
          // BFF 側にも同じ認可がある（[[IADR-0044]] 多層防御）。
          .RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<FeedbackStatsDto>();
    }

    // FR-10: days を [1, MaxStatsDays] にクランプし、集計開始時刻（UTC 当日 00:00 を含む起点）を求める。
    //   DashboardService.SinceUtc と同一のロジック（利用状況と満足率の期間の起点を揃える）。
    private static DateTimeOffset SinceUtc(int days)
    {
        var clamped = Math.Clamp(days, 1, MaxStatsDays);
        var startDate = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-(clamped - 1));
        return new DateTimeOffset(startDate, TimeSpan.Zero);
    }
}
