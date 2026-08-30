using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Features.Dashboard.RecordEvent;

// FR-10: 利用イベント（検索・回答）を記録する。利用者は JWT から特定（テスト・開発は anonymous）。
// 集計の入力となるため、認証済みなら誰でも記録できる（管理者限定にはしないが、認証は必須）。
internal static class RecordUsageEventEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/events", async (UsageEventRequest req, DashboardDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            // バリデーション（入力規則）
            if (!UsageEventType.IsValid(req.EventType))
                return Results.BadRequest(new { error = "eventType must be 'search' or 'answer'" });

            var userId = http.User.Identity?.Name ?? "anonymous";
            var type = UsageEventType.Normalize(req.EventType);
            // 検索語は種別が search のときのみ意味を持つ（answer では保持しない）。
            var query = type == UsageEventType.Search ? Normalize(req.Query) : null;

            var ev = UsageEvent.Create(type, query, userId);
            db.UsageEvents.Add(ev);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/dashboard/events/{ev.Id}", new { ev.Id });
        }).WithName("RecordUsageEvent").RequireAuthorization().Produces(StatusCodes.Status201Created);
    }

    // 検索語の集計キーを安定させるため、前後空白を除去し小文字化する（空は null 扱い）。
    // **記録だけが使う**ため 3 段目に置く（ADR-0068 決定 2）。
    private static string? Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return query.Trim().ToLowerInvariant();
    }
}
