using DashboardService.Domain;
using DashboardService.Features.Dashboard.RecordEvent;
using DashboardService.Features.Dashboard.Summary;
using DashboardService.Features.Dashboard.Trends;
using DashboardService.Features.Dashboard.Usage;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DashboardService.Features.Dashboard;

// FR-10, UC-05: 利用状況・検索傾向・回答品質を可視化するダッシュボード集約の登録表
// （ADR-0068 決定 1）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/Dashboard/<操作>/` に居る（ADR-0065 決定 2）。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group と、
// 2 つ以上の照会が使う集計・期間クランプ・上位件数クランプ。
public static class DashboardEndpoints
{
    // FR-10: 集計期間の既定・上限（無制限な全期間集計を防ぐ）。`SinceUtc` が使う。
    private const int DefaultDays = 7;

    // 🔴 **保持期間（ADR-0072 決定 3）はこの定数から導く**（[[IADR-0368]] (#1198)）。
    // `UsageEventRetention` が `SinceUtc(MaxDays)` を削除の基準時刻に使うため、
    // **上限を動かすと保持期間も同じ値で動く。片方だけは動かせない**
    // （ADR-0072 §残るもの 末尾。片方だけ動かすと、照会できるのに行が無い期間が生じる）。
    // そのため `private` ではなく `internal` である。
    internal const int MaxDays = 90;
    // FR-10: 検索傾向で返す上位件数の既定・上限。`ClampTop` が使う。
    private const int DefaultTop = 10;
    private const int MaxTop = 50;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dashboard").WithTags("Dashboard");

        RecordUsageEventEndpoint.Map(g);
        DashboardUsageEndpoint.Map(g);
        DashboardTrendsEndpoint.Map(g);
        DashboardSummaryEndpoint.Map(g);

        return app;
    }

    // FR-10: 期間内イベントを (日付, 種別) で集計する。日付順・種別順で安定に並べる。
    //   グルーピングはメモリ上で行う（DateOnly 変換はプロバイダ非依存にし、InMemory でも同結果にする）。
    // **利用状況とサマリの 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static async Task<List<UsagePointDto>> AggregateUsageAsync(
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
    // **検索傾向とサマリの 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    //
    // 🔴 **ADR-0071 決定 1（#1197）: 出現件数が `minCount` 未満の語は落とす。**
    // 検索語は自由文であり、個人資料を狙った語・固有名詞・機密文書の題名の断片がそのまま
    // 運用者に読める。計画の原則（内容は出さず件数まで）を画面へ届かせるのがこの述語である。
    //
    // 🔴 **伏せた語を「その他 M 件」へ集約しない。** M 自体が推測の材料になる ——
    // とくに「その他 1 件」は「1 回だけ検索された語が 1 つある」と等価であり、伏せた意味が
    // ほとんど残らない。**落とすだけである。**
    //
    // **述語は `Take(top)` の前に置く。** 後に置くと、上位 `top` 件に紛れ込んだ低件数語を
    // 落とした結果として**返る件数が `top` より減る**（伏せた分だけ枠が空いたままになる）。
    internal static async Task<List<SearchTrendDto>> AggregateTrendsAsync(
        DashboardDbContext db, DateTimeOffset since, int top, int minCount, CancellationToken ct)
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
            .Where(t => t.Count >= minCount)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Term)
            .Take(top)
            .ToList();
    }

    // FR-10, ADR-0071 決定 1: 実際に使う出現件数の下限。不正な構成では既定へ倒し、
    // **倒したことを警告として残す**（[[IADR-0357]]。起動は落とさない —— 秘匿パラメータの
    // 打ち間違いで利用イベントの記録まで巻き添えにしない）。
    // **応答へ添えるのもこの戻り値**である（画面へ嘘の数字を出さない）。
    // **検索傾向とサマリの 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static int EffectiveMinCount(IOptions<SearchTrendOptions> options, ILogger logger)
    {
        var opt = options.Value;
        if (opt.HasInvalidMinimumCount)
            logger.LogWarning(
                "検索傾向の最小件数の構成が不正である（{Configured} 件）。既定の {Default} 件へ倒した。"
                + "構成キーは {Key}:MinimumCount である。",
                opt.MinimumCount, SearchTrendOptions.DefaultMinimumCount, SearchTrendOptions.SectionName);
        return opt.EffectiveMinimumCount;
    }

    // FR-10: days を [1, MaxDays] にクランプし、集計開始時刻（UTC 当日 00:00 を含む起点）を求める。
    // **利用状況・検索傾向・サマリの 3 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static DateTimeOffset SinceUtc(int? days)
    {
        var clamped = Math.Clamp(days ?? DefaultDays, 1, MaxDays);
        var startDate = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-(clamped - 1));
        return new DateTimeOffset(startDate, TimeSpan.Zero);
    }

    // **検索傾向とサマリの 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static int ClampTop(int? top) => Math.Clamp(top ?? DefaultTop, 1, MaxTop);
}
