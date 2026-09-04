using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DashboardService.Domain;
using DashboardService.Features.Dashboard;
using DashboardService.Features.Dashboard.PurgeExpired;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DashboardService.Tests.Features.Dashboard;

// FR-10, UC-05, SC-10, ADR-0072 決定 1・3, [[IADR-0367]] (#1198):
// 利用イベントの主体（持たない）と保持期間（90 日で消す）。
//
// **陽性と陰性を対で置く。** 片方だけだと検査にならない —— 保持日数の述語を丸ごと外して
// 全件削除にしても「91 日前の行が消える」テスト（T-73）は緑のまま通る（変異試験で確認した。
// 落ちるのは T-74 / T-75 の 2 本である）。
public class UsageRetentionTests
{
    // ───────────────────────────────────────────────────────────────────────
    // ADR-0072 決定 1: 利用者識別子を保持しない
    // ───────────────────────────────────────────────────────────────────────

    // T-70 ★ **`UserId` に相当する列が無い**（ADR-0072 決定 1）。
    // モデルを引くのは、列の有無が**マイグレーションではなくモデルで決まる**からである。
    [Fact]
    public async Task UsageEvent_HasNoSubjectColumn()
    {
        using var factory = new TestWebApplicationFactory();
        // 1 件記録してから引く（「行が作れないから列も無い」ではないことを示す）。
        var created = await factory.CreateClient().PostAsJsonAsync(
            "/dashboard/events", new UsageEventRequest("search", "経費規程"),
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var entity = db.Model.FindEntityType(typeof(UsageEvent));
        entity.Should().NotBeNull();

        var properties = entity!.GetProperties().Select(p => p.Name).ToList();
        properties.Should().NotContain("UserId",
            "ADR-0072 決定 1 は利用イベントに利用者識別子を保持しないと定めている");
        // **陽性対照**: 走査そのものが空振りしていない（他の列は引けている）。
        properties.Should().Contain(["Id", "EventType", "Query", "OccurredAt"]);
    }

    // T-71 ★ **未認証は 401**（`RequireAuthorization()` の維持を機械で固定する）。
    // ADR-0072 案 a の却下理由 —— 認証は不正投入の統制であり、記録の統制とは別である。
    [Fact]
    public async Task RecordEvent_Unauthenticated_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/events")
        {
            Content = JsonContent.Create(new UsageEventRequest("search", "検索語"))
        };
        req.Headers.Add(TestAuthHandler.AnonymousHeader, "true");

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ADR-0072 決定 1 は受け口の RequireAuthorization() を維持すると定めている");
    }

    // T-72 ★ **認証済みなら一般利用者でも 201**（管理者限定にはしない。既存の設計を後退させない）。
    // T-71 と対で「誰が呼べるか」を上下から固定する。
    [Fact]
    public async Task RecordEvent_AuthenticatedNonAdmin_Returns201()
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/events")
        {
            Content = JsonContent.Create(new UsageEventRequest("search", "検索語"))
        };
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ───────────────────────────────────────────────────────────────────────
    // ADR-0072 決定 3: 保持期間 90 日（削除の基準時刻は集計の起点と一致）
    // ───────────────────────────────────────────────────────────────────────

    // T-73 ★ **陽性**: 91 日前の行は消える。
    // **日数は絶対値で置く**（基準時刻の式を引かない）—— 90 という値そのものを固定する。
    [Fact]
    public async Task Purge_RemovesEventsOlderThanRetention()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, DateTimeOffset.UtcNow.AddDays(-91), UsageEventType.Search, "古い語");

        var deleted = await PurgeAsync(factory);

        deleted.Should().Be(1);
        (await CountAsync(factory)).Should().Be(0);
    }

    // T-74 ★ **陰性**: 89 日前の行は残り、`GET /dashboard/summary?days=90` に出る。
    // 🔴 **これが変異試験で落ちるテストである**（述語を外して全件削除にすると落ちる）。
    [Fact]
    public async Task Purge_KeepsEventsInsideRetention_AndTheyRemainVisible()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, DateTimeOffset.UtcNow.AddDays(-89), UsageEventType.Search, "新しい語");
        // **陽性対照**: 同じ掃除が古い行は消している（＝掃除が空振りしていない）。
        await SeedAsync(factory, DateTimeOffset.UtcNow.AddDays(-91), UsageEventType.Search, "古い語");

        var deleted = await PurgeAsync(factory);

        deleted.Should().Be(1);
        (await CountAsync(factory)).Should().Be(1);

        var summary = await factory.CreateClient()
            .GetFromJsonAsync<DashboardUsageDto>("/dashboard/summary?days=90",
                TestContext.Current.CancellationToken);
        summary.Should().NotBeNull();
        summary!.TotalSearches.Should().Be(1,
            "削除の基準時刻は集計の起点と一致する（ADR-0072 決定 3）。集計に必要な行は落とさない");
    }

    // T-75 ★ **境界**: 基準時刻**ちょうど**の行は残る（集計が `>=` で読む側と同じ 1 点）。
    [Fact]
    public async Task Purge_AtCutoff_KeepsTheRow()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, UsageEventRetention.CutoffUtc(), UsageEventType.Search, "境界の語");

        var deleted = await PurgeAsync(factory);

        deleted.Should().Be(0);
        (await CountAsync(factory)).Should().Be(1);
    }

    // T-76 ★ **境界**: 基準時刻の 1 ティック前の行は消える。T-75 と対で境界を上下から固定する。
    [Fact]
    public async Task Purge_JustBeforeCutoff_RemovesTheRow()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, UsageEventRetention.CutoffUtc().AddTicks(-1),
            UsageEventType.Search, "境界の 1 つ外の語");

        var deleted = await PurgeAsync(factory);

        deleted.Should().Be(1);
        (await CountAsync(factory)).Should().Be(0);
    }

    // T-77 ★ **不正な構成で起動を落とさず、既定へ倒す**（[[IADR-0357]] / [[IADR-0353]] の作法）。
    // 報告する値も倒した後の値である（ログの周期と実際の周期を食い違わせない）。
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void RetentionOptions_InvalidInterval_FallsBackToDefault(string configured)
    {
        using var factory = new TestWebApplicationFactory
        {
            Settings = new Dictionary<string, string?> { ["UsageRetention:IntervalMinutes"] = configured }
        };

        var options = factory.Services.GetRequiredService<IOptions<UsageRetentionOptions>>().Value;

        options.HasInvalidInterval.Should().BeTrue();
        options.EffectiveIntervalMinutes.Should().Be(UsageRetentionOptions.DefaultIntervalMinutes);
        // **陽性対照**: 構成が読まれていないのではなく、読んだうえで倒している。
        options.IntervalMinutes.Should().Be(int.Parse(configured));
    }

    // T-78 ★ 保持日数は**構成キーを持たない**（ADR-0072 §残るもの 末尾）。
    // 集計の上限と同じ 1 つの定数から来ており、**片方だけは動かせない。**
    [Fact]
    public void RetentionDays_IsTheAggregationCap()
    {
        UsageRetentionOptions.RetentionDays.Should().Be(90);
        UsageEventRetention.CutoffUtc().Should().Be(
            DashboardEndpoints.SinceUtc(90),
            "削除の基準時刻は集計の起点そのものである（ADR-0072 決定 3）");
    }

    // T-79 ★ **常駐処理が実際に消す**（結線の確認。陽性）。
    // 掃除の本体は T-73〜T-76 が測るので、ここで見るのは「回っていること」だけである。
    [Fact]
    public async Task HostedService_WhenEnabled_PurgesOnStart()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, DateTimeOffset.UtcNow.AddDays(-91), UsageEventType.Search, "古い語");

        using var service = CreateHostedService(factory, enabled: true);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForCountAsync(factory, 0);
        await service.StopAsync(TestContext.Current.CancellationToken);

        (await CountAsync(factory)).Should().Be(0);
    }

    // T-80 ★ **陰性**: 無効化すると回らない（`Enabled=false` が効いている）。
    // T-79 と対で置く —— 片方だけだと「そもそも回っていない」と区別できない。
    [Fact]
    public async Task HostedService_WhenDisabled_DoesNotPurge()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, DateTimeOffset.UtcNow.AddDays(-91), UsageEventType.Search, "古い語");

        using var service = CreateHostedService(factory, enabled: false);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        (await CountAsync(factory)).Should().Be(1,
            "UsageRetention:Enabled=false は掃除を止める");
    }

    // ───────────────────────────────────────────────────────────────────────
    // 補助
    // ───────────────────────────────────────────────────────────────────────

    // 発生時刻を指定して 1 件積む。受け口経由では過去の時刻を作れないため、
    // **EF の追跡経由で `OccurredAt` を差し替える**（ドメインへテスト専用の口を開けない）。
    private static async Task SeedAsync(
        TestWebApplicationFactory factory, DateTimeOffset occurredAt, string eventType, string? query)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();

        var ev = UsageEvent.Create(eventType, query);
        db.UsageEvents.Add(ev);
        db.Entry(ev).Property(nameof(UsageEvent.OccurredAt)).CurrentValue = occurredAt;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> PurgeAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<UsageEventRetention>();
        return await retention.PurgeExpiredAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        return await db.UsageEvents.CountAsync(TestContext.Current.CancellationToken);
    }

    // 常駐処理は器の既定では無効である（T-79 だけが有効な器を作る）。
    private static UsageRetentionHostedService CreateHostedService(
        TestWebApplicationFactory factory, bool enabled)
        => new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UsageRetentionOptions { Enabled = enabled, IntervalMinutes = 1 }),
            NullLogger<UsageRetentionHostedService>.Instance);

    // 常駐処理は非同期に回る。**待たずに数えると「まだ消えていない」を「消さない」と読む。**
    private static async Task WaitForCountAsync(TestWebApplicationFactory factory, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(factory) == expected) return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
