using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using GraphService.Features.Clustering.Detect;
using GraphService.Infrastructure.Persistence;
using GraphService.Tests.Common.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GraphService.Tests.Features.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3, ADR-0076 決定 3, [[IADR-0496]] (#1733):
// 日次のクラスタ検出は**前回の成功から 24 時間**で回り、Pod の再起動で 24 時間を数え直さない。
//
// 🔴 #1733 の実測: 初回が起動の 24 時間後（`PeriodicTimer(1 日)`）で、PoC の Pod はほぼ毎日作り直されるため、
// 9/27 から 1 度も検出が走らなかった。本ファイルは次を固定する。
//
//  - T-71: 成功は自分の行に周期の開始時刻を書く。構成が変わらない再検出でも進む（`DetectedAt` では代われない）
//  - T-72: 保存が失敗した周期は成功を記録しない
//  - T-73: 記録が無い・古いなら、起動の待ちの直後に走る（待ちより前には走らない）
//  - T-74: 新しいなら起動では走らず、前回 + 24 時間で走る
//  - T-75: 期限の判定はリースの内側で読み直す（新旧 2 Pod の二重実行を防ぐ）
//  - T-76: 計器・構成・マイグレーション
public sealed class ClusterDetectionCatchUpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static ClusterDetectionMetrics NewMetrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    // ── T-71: 成功の記録 ─────────────────────────────────────────────────────

    // 🔴 FR-17, [[IADR-0496]] 決定 1 (T-71): **構成が変わらない再検出でも成功の時刻は進む。**
    // 対照として `DetectedAt`・`CompositionChangedAt` は進まない（[[IADR-0425]] の不変条件）——
    // だから `max(DetectedAt)` を「最後の実行」に使うと、毎日成功していても止まって見え、再起動のたびに走り直す。
    [Fact]
    public async Task 構成が変わらない再検出でも成功の時刻は進む()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await factory.SeedAsync(db =>
        {
            AddDocuments(db, 2);
            return Task.CompletedTask;
        });

        await RunJobAsync(factory.Services, T0);
        await RunJobAsync(factory.Services, T0.AddDays(1));

        (await LastSucceededAtAsync(factory.Services)).Should().Be(T0.AddDays(1));
        var clusters = await ClustersAsync(factory.Services);
        clusters.Should().HaveCount(2);
        clusters.Should().OnlyContain(c => c.DetectedAt == T0 && c.CompositionChangedAt == T0,
            "構成が変わらない再検出はクラスタの時刻を進めない（対照）");
    }

    // FR-17 (T-71): 文書 0 件（クラスタの行が 1 つも無い）でも成功は記録する。
    [Fact]
    public async Task 文書が無くても成功を記録する()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        (await LastSucceededAtAsync(factory.Services)).Should().BeNull("まだ 1 度も走っていない");

        await RunJobAsync(factory.Services, T0);

        (await LastSucceededAtAsync(factory.Services)).Should().Be(T0);
        (await ClustersAsync(factory.Services)).Should().BeEmpty();
    }

    // ── T-72: 失敗は記録しない ───────────────────────────────────────────────

    // 🔴 FR-17, [[IADR-0496]] 決定 1 (T-72): **クラスタの保存が失敗した周期は成功を記録しない。**
    // 記録をクラスタと別の保存に分ける実装（先に記録だけ保存する）はここで赤になる。陽性対照つき。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task クラスタの保存が失敗した周期は成功を記録しない(bool failClusterSave)
    {
        var dbName = $"GraphCatchUp_{Guid.NewGuid()}";
        await using (var seed = NewContext(dbName))
        {
            AddDocuments(seed, 2);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = NewContext(dbName, failClusterSave ? new FailClusterSaveInterceptor() : null))
        {
            var job = new ClusterDetectionJob(db, new FixedClock(T0), NullLogger<ClusterDetectionJob>.Instance);
            var run = () => job.RunAsync(TestContext.Current.CancellationToken);
            if (failClusterSave)
                await run.Should().ThrowAsync<InvalidOperationException>();
            else
                await run.Should().NotThrowAsync();
        }

        await using var check = NewContext(dbName);
        var rows = await check.BatchRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        if (failClusterSave)
        {
            rows.Should().BeEmpty("失敗した周期は成功を記録しない");
            (await check.Clusters.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        }
        else
        {
            rows.Should().ContainSingle().Which.LastSucceededAt.Should().Be(T0, "陽性対照: 同じ器で成功すれば記録する");
        }
    }

    // ── T-73・T-74: 起動の後の判定 ───────────────────────────────────────────

    // 🔴 FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2 (T-73): **記録が無い・1 周期以上古いなら、起動の待ちの直後に走る。**
    // #1733 の形（起動から 24 時間を数える）では、ここで走らずに赤になる。起動の待ちより前には走らない。
    [Theory]
    [InlineData(null)]
    [InlineData(25)]
    [InlineData(6 * 24)]
    public async Task 記録が無いか古ければ起動の待ちの直後に走る(int? hoursAgo)
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        await SeedAsync(factory.Services, db =>
        {
            AddDocuments(db, 2);
            if (hoursAgo is { } h)
                db.BatchRuns.Add(GraphBatchRun.Create(ClusterDetectionJob.RunName, T0.AddHours(-h)));
        });
        var coordinator = new CountingCoordinator();
        var worker = NewWorker(factory.Services, coordinator, clock);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            (await clock.WaitTimerAsync(Deadline)).Should().BeTrue("起動の待ちのタイマーが作られる");
            clock.Advance(StartupDelay - TimeSpan.FromSeconds(1));
            await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
            coordinator.Calls.Should().Be(0, "起動の待ちより前には判定しない");

            clock.Advance(TimeSpan.FromSeconds(1));
            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue("起動の待ちの直後に判定する");

            (await ClustersAsync(factory.Services)).Should().HaveCount(2, "期限切れなので検出した");
            (await LastSucceededAtAsync(factory.Services)).Should().Be(T0 + StartupDelay, "成功は周期の開始時刻で記録する");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // 🔴 FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2 (T-74): **前回の成功が 1 周期以内なら起動では走らず、前回 + 24 時間で走る。**
    // 再起動のたびに走らせる実装（日次ではなくなる）と、起動から 24 時間を数える実装（#1733）の両方がここで赤になる。
    [Fact]
    public async Task 前回の成功が新しければ前回から1周期後に走る()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        var last = T0.AddHours(-1);
        await SeedAsync(factory.Services, db =>
        {
            AddDocuments(db, 2);
            db.BatchRuns.Add(GraphBatchRun.Create(ClusterDetectionJob.RunName, last));
        });
        var coordinator = new CountingCoordinator();
        var (metrics, probe) = ClusterDetectionMetricsTests.NewProbe();
        using var __ = probe;
        var worker = NewWorker(factory.Services, coordinator, clock, metrics);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            (await clock.WaitTimerAsync(Deadline)).Should().BeTrue();
            clock.Advance(StartupDelay);
            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue("起動の待ちの後に 1 度判定する");
            (await ClustersAsync(factory.Services)).Should().BeEmpty("まだ期限内なので検出しない");
            probe.Observe().Should().Equal([last.ToUnixTimeSeconds()], "判定で読んだ前回の成功を計器へ出す");

            // 期限は「前回 + 24 時間」= 起動から 23 時間後。その 1 分前には走っていない。
            (await clock.WaitTimerAsync(Deadline)).Should().BeTrue("期限までのタイマーが作られる");
            var dueAt = last + ClusterDetectionHostedService.Interval;
            clock.Advance(dueAt - clock.GetUtcNow() - TimeSpan.FromMinutes(1));
            await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
            coordinator.Calls.Should().Be(1, "期限の前には判定し直さない");

            clock.Advance(TimeSpan.FromMinutes(1));
            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue("期限で判定し直す");
            (await ClustersAsync(factory.Services)).Should().HaveCount(2, "期限が来たので検出した");
            (await LastSucceededAtAsync(factory.Services)).Should().Be(dueAt);
            probe.Observe().Should().Equal([dueAt.ToUnixTimeSeconds()], "成功したら計器も進む");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ── T-75: リースの内側で読み直す ─────────────────────────────────────────

    // 🔴 FR-17, [[IADR-0425]] 決定 6, [[IADR-0496]] 決定 3 (T-75): **他の Pod が成功を記録した直後にリースを取っても走らない。**
    // ローリング更新の新旧 2 Pod は同時に起き、同時に「期限切れ」を読む。判定をリースの外で読んだ値で行うと、
    // 先の Pod が検出を終えてリースを放した直後に、後の Pod が 2 回目を走らせる。
    [Fact]
    public async Task 他のPodが成功を記録した後にリースを取っても走らない()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        await SeedAsync(factory.Services, db =>
        {
            AddDocuments(db, 2);
            db.BatchRuns.Add(GraphBatchRun.Create(ClusterDetectionJob.RunName, T0.AddHours(-25)));
        });
        // リースを渡す直前に、他の Pod が検出を終えて成功を記録した状態を作る。
        var coordinator = new PeerRanCoordinator(factory.Services, T0.AddMinutes(-5));
        var worker = NewWorker(factory.Services, coordinator, clock);

        var outcome = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        outcome.Ran.Should().BeFalse();
        outcome.NextDelay.Should().Be(ClusterDetectionHostedService.Interval - TimeSpan.FromMinutes(5));
        (await ClustersAsync(factory.Services)).Should().BeEmpty("他の Pod の成功を見て走らない");
    }

    // FR-17, [[IADR-0496]] 決定 4 (T-75): リースを取れない判定は再試行の待ちの後に判定し直す（読み込みもしない）。
    [Fact]
    public async Task リースを取れない判定は再試行の待ちを返す()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        var worker = NewWorker(factory.Services, new DenyingCoordinator(), clock);

        var outcome = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ClusterDetectionCycleKind.LeaseUnavailable);
        outcome.NextDelay.Should().Be(RetryDelay);
    }

    // ── T-76: 構成とマイグレーション ──────────────────────────────────────────

    [Fact]
    public void 待ちの既定は起動の後2分と失敗の後1時間である()
    {
        var options = new ClusterDetectionOptions();
        options.EffectiveStartupDelay.Should().Be(TimeSpan.FromMinutes(2));
        options.EffectiveRetryDelay.Should().Be(TimeSpan.FromHours(1));
        options.HasInvalidValue.Should().BeFalse();
    }

    // 不正値で起動を落とさず既定へ倒す（購読ごと止めない）。起動の待ち 0 は「待たない」として受ける。
    [Fact]
    public void 不正な待ちは既定へ倒す()
    {
        var invalid = new ClusterDetectionOptions { StartupDelay = TimeSpan.FromSeconds(-1), RetryDelay = TimeSpan.Zero };
        invalid.HasInvalidValue.Should().BeTrue();
        invalid.EffectiveStartupDelay.Should().Be(ClusterDetectionOptions.DefaultStartupDelay);
        invalid.EffectiveRetryDelay.Should().Be(ClusterDetectionOptions.DefaultRetryDelay);

        var zeroStartup = new ClusterDetectionOptions { StartupDelay = TimeSpan.Zero };
        zeroStartup.HasInvalidValue.Should().BeFalse();
        zeroStartup.EffectiveStartupDelay.Should().Be(TimeSpan.Zero);
    }

    // 構成 `ClusterDetection:*` がホストの組み立てで束縛される（試験の器は起動の待ちを 1 日にしている）。
    [Fact]
    public void 待ちは構成から束縛される()
    {
        using var factory = new TestWebApplicationFactory();
        factory.Services.GetRequiredService<IOptions<ClusterDetectionOptions>>().Value.StartupDelay
            .Should().Be(TimeSpan.FromDays(1));
        factory.Services.GetRequiredService<ClusterDetectionMetrics>().Should().NotBeNull();
    }

    // 🔴 (T-76): `graph_batch_runs` を足したマイグレーションがモデルと一致する（作り忘れ・手書きのずれを止める）。
    // 本番の器は起動時のマイグレーションで表を作る。InMemory の試験はこれを通らないので、ここで別に測る。
    [Fact]
    public void マイグレーションがモデルと一致する()
    {
        using var db = new GraphDbContext(new DbContextOptionsBuilder<GraphDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").Options);

        db.Database.HasPendingModelChanges().Should().BeFalse();
        db.Database.GetMigrations().Should().Contain(m => m.EndsWith("_AddGraphBatchRuns", StringComparison.Ordinal));
    }

    // ── 器 ─────────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> NewFactory(TimeProvider clock)
        => new TestWebApplicationFactory().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            // 検出の本体（DI の TimeProvider）とループ（CycleClock）が同じ「今」を見るようにする。
            // タイマーはシステムのまま（偽の時計のタイマーを他の部品に作らせない）。
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new NowFrom(clock));
        }));

    private static ClusterDetectionHostedService NewWorker(
        IServiceProvider services, IClusterDetectionLeaseCoordinator coordinator, TimeProvider clock,
        ClusterDetectionMetrics? metrics = null)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            coordinator,
            Options.Create(new ClusterDetectionOptions { StartupDelay = StartupDelay, RetryDelay = RetryDelay }),
            metrics ?? NewMetrics(),
            NullLogger<ClusterDetectionHostedService>.Instance)
        { CycleClock = clock };

    private static async Task SeedAsync(IServiceProvider services, Action<GraphDbContext> seed)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        seed(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static void AddDocuments(GraphDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
            db.Documents.Add(GraphDocument.Create(Guid.NewGuid(), $"組織文書 {i}", [], bodyHash: null, DateTimeOffset.UnixEpoch));
    }

    private static async Task RunJobAsync(IServiceProvider services, DateTimeOffset now)
    {
        await using var scope = services.CreateAsyncScope();
        var job = new ClusterDetectionJob(
            scope.ServiceProvider.GetRequiredService<GraphDbContext>(),
            new FixedClock(now),
            NullLogger<ClusterDetectionJob>.Instance);
        await job.RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DateTimeOffset?> LastSucceededAtAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ClusterDetectionJob>()
            .ReadLastSucceededAtAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<GraphCluster>> ClustersAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .Clusters.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static GraphDbContext NewContext(string dbName, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<GraphDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new GraphDbContext(builder.Options);
    }

    // クラスタを足す保存だけを失敗させる（記録だけの保存は通す —— 記録を先に別保存する実装を赤にするため）。
    private sealed class FailClusterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<GraphCluster>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("クラスタの保存に失敗した（試験の注入）");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NowFrom(TimeProvider source) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => source.GetUtcNow();
    }

    // 偽の時計。タイマーが作られるたびに知らせる（作られる前に進めた時刻はタイマーに効かない）。
    private sealed class SignalingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly SemaphoreSlim _timerCreated = new(0);

        public Task<bool> WaitTimerAsync(TimeSpan deadline) =>
            _timerCreated.WaitAsync(deadline, TestContext.Current.CancellationToken);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerCreated.Release();
            return timer;
        }
    }

    // リースを渡し、判定の終わり（リースの解放）を知らせる。
    private class CountingCoordinator : IClusterDetectionLeaseCoordinator
    {
        private readonly SemaphoreSlim _cycleEnded = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> WaitCycleEndAsync(TimeSpan deadline) =>
            _cycleEnded.WaitAsync(deadline, TestContext.Current.CancellationToken);

        public virtual Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IAsyncDisposable?>(new Lease(_cycleEnded));
        }

        private sealed class Lease(SemaphoreSlim cycleEnded) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                cycleEnded.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    // リースを渡す直前に、他の Pod の成功を記録する。
    private sealed class PeerRanCoordinator(IServiceProvider services, DateTimeOffset peerSucceededAt) : CountingCoordinator
    {
        public override async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
                var row = await db.BatchRuns.SingleAsync(r => r.JobName == ClusterDetectionJob.RunName, ct);
                row.MarkSucceeded(peerSucceededAt);
                await db.SaveChangesAsync(ct);
            }

            return await base.TryAcquireAsync(ct);
        }
    }

    private sealed class DenyingCoordinator : IClusterDetectionLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }
}
