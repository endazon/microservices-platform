using DashboardService.Api.Domain;
using DashboardService.Api.Infrastructure;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Api.Endpoints;

// FR-10, UC-05: 利用状況・検索傾向・回答品質を可視化するダッシュボードのエンドポイント。
public static class DashboardEndpoints
{
    // FR-10: 集計期間の既定・上限（無制限な全期間集計を防ぐ）。
    private const int DefaultDays = 7;
    private const int MaxDays = 90;
    // FR-10: 検索傾向で返す上位件数の既定・上限。
    private const int DefaultTop = 10;
    private const int MaxTop = 50;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dashboard").WithTags("Dashboard");

        // FR-10: 利用イベント（検索・回答）を記録する。利用者は JWT から特定（テスト・開発は anonymous）。
        // 集計の入力となるため、認証済みなら誰でも記録できる（管理者限定にはしないが、認証は必須）。
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

        // FR-10: 日次利用状況（日付 × 種別の件数）。利用状況グラフの入力。
        // 集計値のみだが利用傾向は運用情報のため、管理者ロールに限定する（FeedbackService 一覧と同方針）。
        g.MapGet("/usage", async (int? days, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = SinceUtc(days);
            var points = await AggregateUsageAsync(db, since, ct);
            return Results.Ok(points);
        }).WithName("DashboardUsage").RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOnly)
          .Produces<List<UsagePointDto>>();

        // FR-10: 検索傾向（よく検索される語の上位）。
        g.MapGet("/trends", async (int? days, int? top, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = SinceUtc(days);
            var trends = await AggregateTrendsAsync(db, since, ClampTop(top), ct);
            return Results.Ok(trends);
        }).WithName("DashboardTrends").RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOnly)
          .Produces<List<SearchTrendDto>>();

        // FR-10: 利用側サマリ（総件数・利用状況・検索傾向）を 1 応答で返す。
        // 回答品質は BFF が FeedbackService から付加して DashboardSummaryDto を組み立てる。
        g.MapGet("/summary", async (int? days, int? top, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = SinceUtc(days);
            var usage = await AggregateUsageAsync(db, since, ct);
            var trends = await AggregateTrendsAsync(db, since, ClampTop(top), ct);
            var totalSearches = usage.Where(p => p.EventType == UsageEventType.Search).Sum(p => p.Count);
            var totalAnswers = usage.Where(p => p.EventType == UsageEventType.Answer).Sum(p => p.Count);
            return Results.Ok(new DashboardUsageDto(totalSearches, totalAnswers, usage, trends));
        }).WithName("DashboardSummary").RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOnly)
          .Produces<DashboardUsageDto>();

        return app;
    }

    // FR-10: 期間内イベントを (日付, 種別) で集計する。日付順・種別順で安定に並べる。
    //   グルーピングはメモリ上で行う（DateOnly 変換はプロバイダ非依存にし、InMemory でも同結果にする）。
    private static async Task<List<UsagePointDto>> AggregateUsageAsync(
        DashboardDbContext db, DateTimeOffset since, CancellationToken ct)
    {
        var rows = await db.UsageEvents
            .Where(u => u.OccurredAt >= since)
            .Select(u => new { u.OccurredAt, u.EventType })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { Date = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime), r.EventType })
            .Select(gr => new UsagePointDto(gr.Key.Date, gr.Key.EventType, gr.Count()))
            .OrderBy(p => p.Date).ThenBy(p => p.EventType)
            .ToList();
    }

    // FR-10: 期間内の検索イベントを検索語で集計し、件数降順の上位を返す。
    //   グルーピングはメモリ上で行う（GroupBy+集計はプロバイダ非依存にし、InMemory でも同結果にする）。
    //   ただし DB からは集計に必要な Query 列のみを射影して取得し、全エンティティのロードは避ける。
    private static async Task<List<SearchTrendDto>> AggregateTrendsAsync(
        DashboardDbContext db, DateTimeOffset since, int top, CancellationToken ct)
    {
        var terms = await db.UsageEvents
            .Where(u => u.OccurredAt >= since
                && u.EventType == UsageEventType.Search
                && u.Query != null && u.Query != "")
            .Select(u => u.Query!)
            .ToListAsync(ct);

        return terms
            .GroupBy(term => term)
            .Select(gr => new SearchTrendDto(gr.Key, gr.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Term)
            .Take(top)
            .ToList();
    }

    // FR-10: days を [1, MaxDays] にクランプし、集計開始時刻（UTC 当日 00:00 を含む起点）を求める。
    private static DateTimeOffset SinceUtc(int? days)
    {
        var clamped = Math.Clamp(days ?? DefaultDays, 1, MaxDays);
        var startDate = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-(clamped - 1));
        return new DateTimeOffset(startDate, TimeSpan.Zero);
    }

    private static int ClampTop(int? top) => Math.Clamp(top ?? DefaultTop, 1, MaxTop);

    // 検索語の集計キーを安定させるため、前後空白を除去し小文字化する（空は null 扱い）。
    private static string? Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return query.Trim().ToLowerInvariant();
    }
}
