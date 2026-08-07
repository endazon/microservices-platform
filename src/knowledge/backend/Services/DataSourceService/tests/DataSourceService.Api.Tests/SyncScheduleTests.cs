using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Api.Foundation.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataSourceService.Api.Tests;

// SC-06（planning#200 / 利用者裁定 2026-08-05 質問票 第12回 Q15）, FR-01, UC-04, IADR-0136:
// 「次回同期」は**共通間隔の次回実行時刻**であり、**全ソースで同じ値**を返す。ソース別スケジュールは持たない。
// 時刻依存は TimeProvider を注入して決定的にする（DateTimeOffset.UtcNow をテストから呼ばない）。
public sealed class SyncScheduleTests
{
    // 検証の基準時刻。どのテストも実時間に依存しない。
    private static readonly DateTimeOffset Anchor = new(2026, 8, 6, 14, 0, 0, TimeSpan.Zero);

    // SC-06, IADR-0136 決定 2: 定期同期が動いていなければ「次回」は無い（無効時に嘘をつかない）。
    [Fact]
    public void NextRunAt_WhenNeverStarted_IsNull()
    {
        var schedule = new SyncSchedule(new FixedTimeProvider(Anchor));

        schedule.NextRunAt.Should().BeNull("定期同期が回っていなければ次回実行時刻は無い");
    }

    // SC-06, UC-04 基本 2: 起動直後の次回は「起点 ＋ 共通間隔」（起動時の 1 回目は今まさに走っている）。
    [Fact]
    public void NextRunAt_JustAfterStart_IsOneIntervalAhead()
    {
        var clock = new FixedTimeProvider(Anchor);
        var schedule = new SyncSchedule(clock);

        schedule.Start(TimeSpan.FromMinutes(5));

        schedule.NextRunAt.Should().Be(Anchor.AddMinutes(5));
    }

    // SC-06, IADR-0136 決定 1: 何周期経っても「現在より後の最初の境界」を返す（過去の時刻を次回と呼ばない）。
    [Theory]
    [InlineData(1, 5)]    // 起点 +1 分 → 次は +5 分
    [InlineData(7, 10)]   // 起点 +7 分（1 周期経過）→ 次は +10 分
    [InlineData(23, 25)]  // 起点 +23 分（4 周期経過）→ 次は +25 分
    public void NextRunAt_AfterSeveralIntervals_IsNextBoundary(int elapsedMinutes, int expectedMinutes)
    {
        var clock = new FixedTimeProvider(Anchor);
        var schedule = new SyncSchedule(clock);
        schedule.Start(TimeSpan.FromMinutes(5));

        clock.UtcNow = Anchor.AddMinutes(elapsedMinutes);

        schedule.NextRunAt.Should().Be(Anchor.AddMinutes(expectedMinutes));
        schedule.NextRunAt.Should().BeAfter(clock.UtcNow, "次回は常に現在より後である");
    }

    // 境界ちょうどの瞬間は「その回」が走っている時刻なので、次回は 1 つ先の境界になる。
    [Fact]
    public void NextRunAt_ExactlyOnBoundary_MovesToNextBoundary()
    {
        var clock = new FixedTimeProvider(Anchor);
        var schedule = new SyncSchedule(clock);
        schedule.Start(TimeSpan.FromMinutes(5));

        clock.UtcNow = Anchor.AddMinutes(5);

        schedule.NextRunAt.Should().Be(Anchor.AddMinutes(10));
    }

    // IADR-0136 決定 2: 無効なワーカーは位相を記録しない（＝/datasources は nextSyncAt を null で返す）。
    [Fact]
    public void StartSchedule_WhenDisabled_LeavesScheduleUnset()
    {
        var schedule = new SyncSchedule(new FixedTimeProvider(Anchor));
        var worker = BuildWorker(schedule, new DataSourceSyncOptions { Enabled = false });

        var interval = worker.StartSchedule();

        interval.Should().BeNull("無効なワーカーは回らない");
        schedule.NextRunAt.Should().BeNull("回らないワーカーの次回実行時刻は無い");
    }

    // IADR-0051 / IADR-0136 決定 1: 有効なワーカーは起動時刻を起点に共通間隔を刻み始める。
    [Fact]
    public void StartSchedule_WhenEnabled_AnchorsAtStartup()
    {
        var schedule = new SyncSchedule(new FixedTimeProvider(Anchor));
        var worker = BuildWorker(schedule,
            new DataSourceSyncOptions { Enabled = true, IntervalSeconds = 600 });

        var interval = worker.StartSchedule();

        interval.Should().Be(TimeSpan.FromMinutes(10));
        schedule.NextRunAt.Should().Be(Anchor.AddMinutes(10));
    }

    // 過負荷防止の 30 秒床は PeriodicTimer だけでなく「次回同期」の表示にも効く（同じ実効間隔を使う）。
    [Theory]
    [InlineData(5, 30)]     // 設定が小さくても実効は 30 秒
    [InlineData(30, 30)]
    [InlineData(300, 300)]  // 既定
    public void StartSchedule_FloorsIntervalAtThirtySeconds(int configured, int effectiveSeconds)
    {
        var schedule = new SyncSchedule(new FixedTimeProvider(Anchor));
        var worker = BuildWorker(schedule,
            new DataSourceSyncOptions { Enabled = true, IntervalSeconds = configured });

        worker.StartSchedule();

        schedule.NextRunAt.Should().Be(Anchor.AddSeconds(effectiveSeconds));
    }

    // SC-06 裁定 Q15 の核心: 一覧の**全ソースが同じ値**を返す（ソース別スケジュールを持たない）。
    [Fact]
    public async Task ListDataSources_ReturnsSameNextSyncAtForEverySource()
    {
        var schedule = new SyncSchedule(new FixedTimeProvider(Anchor));
        schedule.Start(TimeSpan.FromMinutes(5));
        using var factory = new ScheduledFactory(schedule);
        var client = factory.CreateClient();

        await CreateDataSourceAsync(client, "共有フォルダ", "filesystem");
        await CreateDataSourceAsync(client, "社内 Wiki", "wiki");

        var list = await client.GetAsync("/datasources");
        list.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        var values = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("nextSyncAt").GetDateTimeOffset())
            .ToList();

        values.Should().HaveCountGreaterThanOrEqualTo(2);
        values.Distinct().Should().ContainSingle(
            "次回同期は共通間隔の次回実行時刻であり全ソース同値である（ソース別スケジュールは持たない）")
            .Which.Should().Be(Anchor.AddMinutes(5));
    }

    // IADR-0136 決定 2: 定期同期が無効な構成（compose / dev の既定）では null を返す。
    [Fact]
    public async Task ListDataSources_WhenPeriodicSyncDisabled_ReturnsNullNextSyncAt()
    {
        // 既定の factory は DataSourceSync:Enabled=false のままなので位相は記録されない。
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await CreateDataSourceAsync(client, "無効時ソース", "filesystem");

        var list = await client.GetAsync("/datasources");
        list.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("nextSyncAt").ValueKind)
            .Should().OnlyContain(kind => kind == JsonValueKind.Null,
                "定期同期が無効なら「次回」は無い（あると偽らない）");
    }

    private static async Task CreateDataSourceAsync(HttpClient client, string name, string sourceType)
    {
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name,
            sourceType,
            connectionUri = "file://share/docs",
        });
        resp.EnsureSuccessStatusCode();
    }

    private static DataSourceSyncHostedService BuildWorker(SyncSchedule schedule, DataSourceSyncOptions options) =>
        new(
            scopeFactory: null!,        // StartSchedule は同期サイクルを回さないため未使用
            leaseCoordinator: null!,    // 同上
            schedule,
            Options.Create(options),
            NullLogger<DataSourceSyncHostedService>.Instance);

    // 起動済みの SyncSchedule を差し込む factory（後から登録した singleton が解決に勝つ）。
    private sealed class ScheduledFactory(SyncSchedule schedule) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton(schedule));
        }
    }

    // 固定時計。テストが進めたいときだけ UtcNow を書き換える。
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
