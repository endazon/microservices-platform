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
//  - T-77: 失敗が続けば再試行の待ちを倍々にする。試行は本体の前に記録し、プロセスごと落ちた試行も次の起動が数える
//  - T-78: 本体の保存は 1 回。成功後の待ちは周期の開始時刻から 1 周期（下限は再試行の待ち）
//  - T-79: 停止要求は走っている判定へ届く
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
                db.BatchRuns.Add(GraphBatchRun.Succeeded(ClusterDetectionJob.RunName, T0.AddHours(-h)));
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
            db.BatchRuns.Add(GraphBatchRun.Succeeded(ClusterDetectionJob.RunName, last));
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
            db.BatchRuns.Add(GraphBatchRun.Succeeded(ClusterDetectionJob.RunName, T0.AddHours(-25)));
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

        // 上限（1 日）を超える待ちも既定へ倒す（`Task.Delay` の上限を超える値でループの外へ例外が漏れない）。
        var tooLong = new ClusterDetectionOptions { StartupDelay = TimeSpan.FromDays(60), RetryDelay = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1) };
        tooLong.HasInvalidValue.Should().BeTrue();
        tooLong.EffectiveStartupDelay.Should().Be(ClusterDetectionOptions.DefaultStartupDelay);
        tooLong.EffectiveRetryDelay.Should().Be(ClusterDetectionOptions.DefaultRetryDelay);
        new ClusterDetectionOptions { StartupDelay = TimeSpan.FromDays(1), RetryDelay = TimeSpan.FromDays(1) }
            .HasInvalidValue.Should().BeFalse("上限ちょうどは受ける（試験の器は起動の待ちを 1 日にしている）");

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

    // ── T-77: 失敗のバックオフと試行の記録 ───────────────────────────────────

    // 🔴 FR-17, [[IADR-0496]] 決定 4 (T-77): **失敗が続けば再試行の待ちを倍々にする**（1 時間 → 2 → 4。周期 24 時間とは別の値）。
    // 失敗の後の待ちを周期にする変異（N7）と、倍々にしない変異をここで落とす。各回の手前では判定しない。
    [Fact]
    public async Task 失敗が続くと再試行の待ちを倍々にする()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        await SeedAsync(factory.Services, db => AddDocuments(db, 2));
        var coordinator = new FailingThenGrantingCoordinator(clock, failures: 3);
        var worker = NewWorker(factory.Services, coordinator, clock);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            TimeSpan[] waits = [StartupDelay, RetryDelay, 2 * RetryDelay, 4 * RetryDelay];
            for (var i = 0; i < waits.Length; i++)
            {
                (await clock.WaitTimerAsync(Deadline)).Should().BeTrue($"{i + 1} 回目の待ちのタイマーが作られる");
                clock.Advance(waits[i] - TimeSpan.FromSeconds(1));
                await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
                coordinator.AcquiredAt.Should().HaveCount(i, $"{i + 1} 回目の待ちの手前では判定しない");
                clock.Advance(TimeSpan.FromSeconds(1));
                (await coordinator.WaitCallAsync(Deadline)).Should().BeTrue($"{i + 1} 回目の判定が来る");
            }

            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue("4 回目はリースを取って検出する");
            coordinator.AcquiredAt.Should().Equal(
                T0 + StartupDelay,
                T0 + StartupDelay + RetryDelay,
                T0 + StartupDelay + 3 * RetryDelay,
                T0 + StartupDelay + 7 * RetryDelay);
            (await ClustersAsync(factory.Services)).Should().HaveCount(2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // 🔴 FR-17, [[IADR-0496]] 決定 4 (T-77): **前の試行が成功しないまま終わった記録が残っていれば、起動の後もバックオフを待つ。**
    // プロセスごと落ちた（メモリ不足等）試行は例外として捕まらない。プロセス内の数だけでバックオフすると、
    // 「再起動 → 起動の待ち → 再実行 → 落ちる」を繰り返す。成功していない試行 2 回・最後の試行 30 分前なら、
    // 待ちは 2 時間 − 30 分。成功の記録は無い（期限切れ）ので、バックオフが無ければ起動の待ちの直後に走る。
    [Fact]
    public async Task 成功しなかった試行の記録が残っていれば起動の後もバックオフを待つ()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        var lastAttempt = T0.AddMinutes(-30);
        await SeedAsync(factory.Services, db =>
        {
            AddDocuments(db, 2);
            var run = GraphBatchRun.Attempted(ClusterDetectionJob.RunName, lastAttempt.AddHours(-1));
            run.MarkAttempted(lastAttempt);
            db.BatchRuns.Add(run);
        });
        var coordinator = new CountingCoordinator();
        var worker = NewWorker(factory.Services, coordinator, clock);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            (await clock.WaitTimerAsync(Deadline)).Should().BeTrue();
            clock.Advance(StartupDelay);
            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue();
            (await ClustersAsync(factory.Services)).Should().BeEmpty("前の試行から 2 時間たっていない");

            var backoffUntil = lastAttempt + 2 * RetryDelay;
            (await clock.WaitTimerAsync(Deadline)).Should().BeTrue();
            clock.Advance(backoffUntil - clock.GetUtcNow() - TimeSpan.FromMinutes(1));
            await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
            coordinator.Calls.Should().Be(1, "バックオフの手前では判定し直さない");

            clock.Advance(TimeSpan.FromMinutes(1));
            (await coordinator.WaitCycleEndAsync(Deadline)).Should().BeTrue();
            (await ClustersAsync(factory.Services)).Should().HaveCount(2, "バックオフが明けたので検出した");
            var state = await RunStateAsync(factory.Services);
            state.LastSucceededAt.Should().Be(backoffUntil);
            state.AttemptsSinceSuccess.Should().Be(0, "成功で数が戻る");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // 🔴 FR-17, [[IADR-0496]] 決定 1・4 (T-77): **本体が失敗した判定は、試行だけを記録し、成功を記録しない。**
    // 試行は本体より先に別の保存で確定させる（落ちても残す）。成功は本体と同じ保存なので巻き戻る。
    [Fact]
    public async Task 本体が失敗した判定は試行だけを記録する()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock, interceptor: new FailClusterSaveInterceptor());
        await SeedAsync(factory.Services, db => AddDocuments(db, 2));
        var worker = NewWorker(factory.Services, new CountingCoordinator(), clock);

        var cycle = () => worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        await cycle.Should().ThrowAsync<InvalidOperationException>();
        var state = await RunStateAsync(factory.Services);
        state.LastSucceededAt.Should().BeNull("失敗した判定は成功を記録しない");
        state.LastAttemptedAt.Should().Be(T0, "試行は本体の前に記録される");
        state.AttemptsSinceSuccess.Should().Be(1);
        (await ClustersAsync(factory.Services)).Should().BeEmpty();
    }

    // ── T-78: 保存の回数と成功後の待ち ───────────────────────────────────────

    // 🔴 FR-17, [[IADR-0496]] 決定 1 (T-78): **検出の本体は保存を 1 回だけ行う**（クラスタと成功の記録が同じ保存）。
    // 成功の記録をクラスタの後の別の保存に分ける変異（N12）は、T-72（クラスタの保存の失敗）では落ちないのでここで落とす。
    [Fact]
    public async Task 検出の本体は保存を1回だけ行う()
    {
        var dbName = $"GraphCatchUp_{Guid.NewGuid()}";
        await using (var seed = NewContext(dbName))
        {
            AddDocuments(seed, 2);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var counter = new SaveCountingInterceptor();
        await using (var db = NewContext(dbName, counter))
        {
            var job = new ClusterDetectionJob(db, new FixedClock(T0), NullLogger<ClusterDetectionJob>.Instance);
            await job.RunAsync(TestContext.Current.CancellationToken);
        }

        counter.Saves.Should().Be(1);
        await using var check = NewContext(dbName);
        (await check.BatchRuns.SingleAsync(TestContext.Current.CancellationToken)).LastSucceededAt.Should().Be(T0);
        (await check.Clusters.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    // 🔴 FR-17, [[IADR-0496]] 決定 2 (T-78): **成功の後の待ちは、周期の開始時刻（記録した時刻）から 1 周期**である。
    // 本体の開始時刻を判定の時刻より 3 時間前にずらすと、待ちは 21 時間になる（周期をそのまま返す変異 N3 は 24 時間を返す）。
    [Fact]
    public async Task 成功の後の待ちは周期の開始時刻から1周期である()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock, jobClockOffset: TimeSpan.FromHours(-3));
        await SeedAsync(factory.Services, db => AddDocuments(db, 2));
        var worker = NewWorker(factory.Services, new CountingCoordinator(), clock);

        var outcome = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        outcome.Ran.Should().BeTrue();
        outcome.NextDelay.Should().Be(TimeSpan.FromHours(21));
    }

    // FR-17, [[IADR-0496]] 決定 2 (T-78): 成功の後の待ちの**下限は再試行の待ち**である（周期より長くかかった検出が間を空けずに回り続けない）。
    // 周期を 30 分（試験だけの値）にすると、開始時刻からの残りは 30 分未満なので、待ちは再試行の 1 時間になる（下限を外す変異 N2 は 30 分を返す）。
    [Fact]
    public async Task 成功の後の待ちの下限は再試行の待ちである()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        await SeedAsync(factory.Services, db => AddDocuments(db, 2));
        var worker = NewWorker(factory.Services, new CountingCoordinator(), clock, cycleInterval: TimeSpan.FromMinutes(30));

        var outcome = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        outcome.Ran.Should().BeTrue();
        outcome.NextDelay.Should().Be(RetryDelay);
    }

    // ── T-79: 停止要求 ──────────────────────────────────────────────────────

    // FR-17, [[IADR-0299]] 決定 3, [[IADR-0496]] 決定 4 (T-79): **停止要求は走っている判定へ届き、ループは静かに終わる。**
    // 判定へ停止のトークンを渡さない変異（N16）は、取得が取り消されないまま止まらない。
    [Fact]
    public async Task 停止要求は走っている判定へ届く()
    {
        var clock = new SignalingClock(T0);
        using var factory = NewFactory(clock);
        var coordinator = new BlockingCoordinator();
        var worker = NewWorker(factory.Services, coordinator, clock);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        (await clock.WaitTimerAsync(Deadline)).Should().BeTrue();
        clock.Advance(StartupDelay);
        await coordinator.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        using var stopDeadline = new CancellationTokenSource(Deadline);
        await worker.StopAsync(stopDeadline.Token);

        coordinator.Cancelled.Task.IsCompleted.Should().BeTrue("停止のトークンが判定（リースの取得）まで届いている");
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして静かに終える");
    }

    // ── 器 ─────────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> NewFactory(
        TimeProvider clock, TimeSpan jobClockOffset = default, IInterceptor? interceptor = null)
        => new TestWebApplicationFactory().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            // 検出の本体（DI の TimeProvider）とループ（CycleClock）が同じ「今」を見るようにする（`jobClockOffset` だけずらせる）。
            // タイマーはシステムのまま（偽の時計のタイマーを他の部品に作らせない）。
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(new NowFrom(clock, jobClockOffset));
            if (interceptor is not null)
                s.ConfigureDbContext<GraphDbContext>(o => o.AddInterceptors(interceptor));
        }));

    private static ClusterDetectionHostedService NewWorker(
        IServiceProvider services, IClusterDetectionLeaseCoordinator coordinator, TimeProvider clock,
        ClusterDetectionMetrics? metrics = null, TimeSpan? cycleInterval = null)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            coordinator,
            Options.Create(new ClusterDetectionOptions { StartupDelay = StartupDelay, RetryDelay = RetryDelay }),
            metrics ?? NewMetrics(),
            NullLogger<ClusterDetectionHostedService>.Instance)
        { CycleClock = clock, CycleInterval = cycleInterval ?? ClusterDetectionHostedService.Interval };

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
        return (await RunStateAsync(services)).LastSucceededAt;
    }

    private static async Task<ClusterDetectionRunState> RunStateAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ClusterDetectionJob>()
            .ReadRunStateAsync(TestContext.Current.CancellationToken);
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

    private sealed class NowFrom(TimeProvider source, TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => source.GetUtcNow() + offset;
    }

    private sealed class SaveCountingInterceptor : SaveChangesInterceptor
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // 最初の `failures` 回の取得で投げ、以後はリースを渡す。各回が見た偽の時計の時刻を記録する。
    private sealed class FailingThenGrantingCoordinator(TimeProvider clock, int failures) : CountingCoordinator
    {
        private readonly List<DateTimeOffset> _acquiredAt = [];
        private readonly SemaphoreSlim _called = new(0);
        private int _n;

        public IReadOnlyList<DateTimeOffset> AcquiredAt { get { lock (_acquiredAt) return [.. _acquiredAt]; } }

        public Task<bool> WaitCallAsync(TimeSpan deadline) =>
            _called.WaitAsync(deadline, TestContext.Current.CancellationToken);

        public override Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            lock (_acquiredAt) _acquiredAt.Add(clock.GetUtcNow());
            var n = Interlocked.Increment(ref _n);
            _called.Release();
            return n <= failures
                ? Task.FromException<IAsyncDisposable?>(new InvalidOperationException("リースの取得に失敗（試験の注入）"))
                : base.TryAcquireAsync(ct);
        }
    }

    // 取得の中で停止要求を待つ。取り消されたら知らせる（N16: 停止のトークンが周期へ届くこと）。
    private sealed class BlockingCoordinator : IClusterDetectionLeaseCoordinator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return null;
        }
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
