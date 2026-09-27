using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AuthorizationService.Domain;
using AuthorizationService.Features.Authz.OwnerReadPolicyGuard;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Features.Authz.OwnerReadPolicyGuard;

// FR-05, FR-19, NFR-09, NFR-21, 計画 ADR-0121 決定 2・4・フォローアップ 2, [[IADR-0481]] (#1665):
// **所有者の読み取りのポリシーが消えたら検知して知らせる**定期の検査（issue のやること 2・4）。
// 知らせる経路は計器 → 警報（`OwnerReadPolicyMissing` / `OwnerReadPolicyCheckSeriesAbsent`）と Error ログである。
[Trait("TestKind", "Unit")]
public class OwnerReadPolicyCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorizationDbContext NewDb()
        => new(new DbContextOptionsBuilder<AuthorizationDbContext>()
            .UseInMemoryDatabase($"OwnerReadPolicyCheck_{Guid.NewGuid()}").Options);

    private static AbacPolicy Owner(bool active = true, string key = "owner")
    {
        var p = AbacPolicy.Create("所有者は自分の文書を読める", PolicyAction.Read, [],
            new Dictionary<string, List<string>> { [key] = ["${current_user}"] });
        if (!active) p.SetActive(false);
        return p;
    }

    // 計器は自分の IMeterFactory の器だけを聞く（同じ Meter 名の他の試験の測定を混ぜない）。
    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _outcomes = new();
        private readonly List<int> _gauge = [];

        public Probe(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory)
                    && instrument.Meter.Name == OwnerReadPolicyMetrics.MeterName
                    && instrument.Name is OwnerReadPolicyMetrics.ActiveGaugeName or OwnerReadPolicyMetrics.CheckCounterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == OwnerReadPolicyMetrics.OutcomeTag)
                        _outcomes.AddOrUpdate((string)tag.Value!, value, (_, v) => v + value);
            });
            _listener.SetMeasurementEventCallback<int>((_, value, _, _) =>
            {
                lock (_gauge) _gauge.Add(value);
            });
            _listener.Start();
        }

        public long Outcome(string outcome) => _outcomes.GetValueOrDefault(outcome);

        // ゲージを 1 度収集し、出た値（出なければ空）を返す。
        public IReadOnlyList<int> CollectGauge()
        {
            lock (_gauge) _gauge.Clear();
            _listener.RecordObservableInstruments();
            lock (_gauge) return [.. _gauge];
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
    }

    private static (OwnerReadPolicyCheck Check, OwnerReadPolicyMetrics Metrics, Probe Probe, RecordingLogger<OwnerReadPolicyCheck> Log)
        Arrange(AuthorizationDbContext db)
    {
        var factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var probe = new Probe(factory);
        var metrics = new OwnerReadPolicyMetrics(factory);
        var log = new RecordingLogger<OwnerReadPolicyCheck>();
        return (new OwnerReadPolicyCheck(db, metrics, log), metrics, probe, log);
    }

    // T-41: 陽性対照。在るならゲージは件数、結末は present、Error は出ない。
    [Fact]
    public async Task 在るときはゲージが件数を出しErrorを出さない()
    {
        using var db = NewDb();
        db.Policies.Add(Owner());
        await db.SaveChangesAsync(Ct);
        var (check, _, probe, log) = Arrange(db);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().Be(1);

        probe.CollectGauge().Should().Equal(1);
        probe.Outcome(OwnerReadPolicyMetrics.OutcomePresent).Should().Be(1);
        log.Count(LogLevel.Error).Should().Be(0);
    }

    public static TheoryData<string, AbacPolicy[]> Missing() => new()
    {
        { "無い", [] },
        { "無効", [Owner(active: false)] },
        { "形が違う（キーが Owner）", [Owner(key: "Owner")] },
    };

    // T-42: 否定の試験。無い・無効・形が違うなら、ゲージは 0（警報 OwnerReadPolicyMissing の入力）、結末は absent、Error を出す。
    [Theory]
    [MemberData(nameof(Missing))]
    public async Task 無い無効形違いはゲージ0とErrorで知らせる(string _, AbacPolicy[] policies)
    {
        using var db = NewDb();
        db.Policies.AddRange(policies);
        await db.SaveChangesAsync(Ct);
        var (check, _, probe, log) = Arrange(db);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().Be(0);

        probe.CollectGauge().Should().Equal(0);
        probe.Outcome(OwnerReadPolicyMetrics.OutcomeAbsent).Should().Be(1);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain("所有者の読み取りのポリシーが有効な状態で 1 件も無い")
            .And.Contain("${current_user}");
    }

    // T-43: 数えられない（ポリシーの表を読めない）ときは、ゲージの系列を止める（0 も古い値も出さない）。
    // 系列の不在は警報 OwnerReadPolicyCheckSeriesAbsent が「見ていない」として知らせる。
    [Fact]
    public async Task 数えられないときはゲージの系列を止めてErrorを出す()
    {
        var db = NewDb();
        db.Policies.Add(Owner());
        await db.SaveChangesAsync(Ct);
        var (check, metrics, probe, log) = Arrange(db);
        using var _p = probe;
        (await check.RunAsync(Ct)).Should().Be(1);
        probe.CollectGauge().Should().Equal(1);

        await db.DisposeAsync(); // 表を読めなくする

        (await check.RunAsync(Ct)).Should().BeNull();

        probe.CollectGauge().Should().BeEmpty("直近の検査に失敗したら、前の値（1）も 0 も出さない");
        metrics.LastCount.Should().BeNull();
        probe.Outcome(OwnerReadPolicyMetrics.OutcomeFailed).Should().Be(1);
        log.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void 未検査のあいだはゲージの系列を出さない()
    {
        using var db = NewDb();
        var (_, _, probe, _) = Arrange(db);
        using var _p = probe;

        probe.CollectGauge().Should().BeEmpty();
    }

    // T-44: 消えて戻ったら、戻ったことを Information で残す（Error は消えた周期だけ）。
    [Fact]
    public async Task 消えて戻るとゲージが追随し戻りを記録する()
    {
        using var db = NewDb();
        var policy = Owner();
        db.Policies.Add(policy);
        await db.SaveChangesAsync(Ct);
        var (check, _, probe, log) = Arrange(db);
        using var _p = probe;

        (await check.RunAsync(Ct)).Should().Be(1);
        policy.SetActive(false);
        await db.SaveChangesAsync(Ct);
        (await check.RunAsync(Ct)).Should().Be(0);
        probe.CollectGauge().Should().Equal(0);
        policy.SetActive(true);
        await db.SaveChangesAsync(Ct);
        (await check.RunAsync(Ct)).Should().Be(1);

        probe.CollectGauge().Should().Equal(1);
        log.Count(LogLevel.Error).Should().Be(1);
        log.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("戻った"));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("00:05:00", 5)]
    public void 周期の構成を読む(string? declared, int minutes)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [OwnerReadPolicyCheckOptions.IntervalKey] = declared })
            .Build();
        OwnerReadPolicyCheckOptions.FromConfiguration(config).Interval.Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("60")]
    [InlineData("00:00:30")]
    [InlineData("24:00:00")]
    [InlineData("abc")]
    public void 周期の値域外は起動時に落ちる(string declared)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [OwnerReadPolicyCheckOptions.IntervalKey] = declared })
            .Build();
        var act = () => OwnerReadPolicyCheckOptions.FromConfiguration(config);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{OwnerReadPolicyCheckOptions.IntervalKey}*");
    }
}

// T-45: 実際のホストで、**構成なしで**（opt-in にせず）起動時に 1 回検査が走る（門より前に働いている）。
[Trait("TestKind", "Integration")]
public class OwnerReadPolicyCheckHostingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OwnerReadPolicyCheckHostingTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact(Timeout = 30_000)]
    public async Task 構成なしでも起動時に検査が走る()
    {
        var metrics = _factory.Services.GetRequiredService<OwnerReadPolicyMetrics>();
        for (var i = 0; i < 500 && metrics.LastCount is null; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        metrics.LastCount.Should().Be(0, "空の DB で起動したので、検査は走って「無い」と数えている");
    }
}
