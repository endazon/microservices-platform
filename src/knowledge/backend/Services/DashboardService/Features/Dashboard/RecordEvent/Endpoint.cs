using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Features.Dashboard.RecordEvent;

// FR-10: 利用イベント（検索・回答）を記録する。
// 集計の入力となるため、認証済みなら誰でも記録できる（管理者限定にはしないが、認証は必須）。
//
// 🔴 **記録する主体は列へ書かない**（ADR-0072 決定 1・[[IADR-0368]] (#1198)）。
// 変わったのは「解決した主体を列へ書くこと」だけであり、**`RequireAuthorization()` は維持する**
// —— 認証は不正投入の統制であり、記録の統制とは別である（案 a の却下理由）。
// 一緒に外すと誰でも利用イベントを投げられる。
internal static class RecordUsageEventEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/events", async (UsageEventRequest req, DashboardDbContext db,
            CancellationToken ct) =>
        {
            // バリデーション（入力規則）
            if (!UsageEventType.IsValid(req.EventType))
                return Results.BadRequest(new { error = "eventType must be 'search' or 'answer'" });

            var type = UsageEventType.Normalize(req.EventType);
            // 検索語は種別が search のときのみ意味を持つ（answer では保持しない）。
            var query = type == UsageEventType.Search ? Normalize(req.Query) : null;

            var ev = UsageEvent.Create(type, query);
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
