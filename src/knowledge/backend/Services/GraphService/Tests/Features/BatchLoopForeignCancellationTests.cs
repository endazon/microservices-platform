using System.Collections.Concurrent;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Features.Clustering.Detect;
using GraphService.Features.Clustering.Summarize;
using GraphService.Features.KnowledgeHealth.Report;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GraphService.Tests.Features;

// FR-17, FR-18, FR-10, ADR-0035 決定 3, [[IADR-0299]] 決定 3 の 2026-09-26 追記, [[IADR-0425]], [[IADR-0430]] (#1598):
// GraphService の 3 つの定期処理は、**停止要求ではない取り消し**（下流の時間切れ等）で周期のループを終えない。
//
// 🔴 直す前の形は、周期の本体の `catch (Exception) when (ex is not OperationCanceledException)` が取り消しを素通しし、
// 外側の型だけの `catch (OperationCanceledException)` が「シャットダウン」と読んでループを**永久に**終えていた
// （日次のクラスタ検出・要約、毎時の健全性の報告が、プロセスが生きたまま止まる）。
// 3 つは同じ形なので同じ試験を 1 つずつ置く（1 つだけ直す変異を他の 2 つが通さない）。
//
// 周期は `CycleInterval`（試験だけが短くする口）で数十ミリ秒にし、1 周期目の最初の呼び出し（リースの取得）で
// 停止要求と無関係な取り消しを投げ、**2 周期目のリースの取得が起きる**ことを上限つきで待つ。
// 2 周期目以降はリースを渡さない（＝本体は DB を読まない）ので、スコープは空の入れ物でよい。
public sealed class BatchLoopForeignCancellationTests
{
    private static readonly TimeSpan ShortCycle = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task クラスタ検出は停止要求でない取り消しの後も次の周期を回す()
    {
        var coordinator = new ForeignCancellationCoordinator();
        var logger = new RecordingLogger<ClusterDetectionHostedService>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterDetectionHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator, logger)
        { CycleInterval = ShortCycle };

        await AssertLoopSurvivesAsync(worker, coordinator, logger.Errors);
    }

    [Fact]
    public async Task クラスタ要約は停止要求でない取り消しの後も次の周期を回す()
    {
        var coordinator = new ForeignCancellationCoordinator();
        var logger = new RecordingLogger<ClusterSummaryHostedService>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterSummaryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            Options.Create(new ClusterSummaryOptions { Enabled = true }), logger)
        { CycleInterval = ShortCycle };

        await AssertLoopSurvivesAsync(worker, coordinator, logger.Errors);
    }

    [Fact]
    public async Task ナレッジ健全性の報告は停止要求でない取り消しの後も次の周期を回す()
    {
        var coordinator = new ForeignCancellationCoordinator();
        var logger = new RecordingLogger<KnowledgeHealthHostedService>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new KnowledgeHealthHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator, logger)
        { CycleInterval = ShortCycle };

        await AssertLoopSurvivesAsync(worker, coordinator, logger.Errors);
    }

    // ［#1604］FR-17, FR-18, FR-10, [[IADR-0299]] 決定 3 の追記: 失敗した周期の後は**次の拍まで待つ**（間を空けずに再試行しない）。
    // #1598 の試験は「次の周期が来る」ことしか測っておらず、失敗の直後に待たずに再試行する変異（M1）が生き残った（#1601 の監査）。
    // 1・2 回目のリースの取得で投げ、3 回目までの各回が**別の拍**で起きることを測る。
    //
    // ［#1622］FR-17, FR-18, FR-10, [[IADR-0299]] 決定 3 の追記: 拍は**偽の時計**（`CycleClock` に `FakeTimeProvider`）で試験が手で進める。
    // 従前は周期 300 ミリ秒の実時間で「各回の間隔が周期の半分以上」を測っており、1 周期目の本体が負荷で遅れると `PeriodicTimer` が溜まった拍を
    // すぐに発火し、**正しい実装でも**間隔が縮んで落ちた。偽の時計は試験が進めない限り進まないので、拍が溜まることは無い。
    // 判定: (a) 拍を進める前は、次の取得が来ない（失敗の後に静穏の窓を置いて回数を見る）、(b) k 回目の取得が見た偽の時刻が「開始 + k 周期」。
    // 正しい実装では (a)(b) とも決定的に成り立つ（窓の長さに依らない）。窓が効くのは M1 の側だけで、M1 は失敗の直後に拍を待たずに取得する。
    private static readonly TimeSpan TickCycle = TimeSpan.FromHours(1);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task クラスタ検出は失敗が続いても次の拍まで待って回す()
    {
        var clock = new ManualTickClock();
        var coordinator = new ThrowTwiceCoordinator(clock);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterDetectionHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            new RecordingLogger<ClusterDetectionHostedService>())
        { CycleInterval = TickCycle, CycleClock = clock };

        await AssertWaitsForNextTickAsync(worker, clock, coordinator);
    }

    [Fact]
    public async Task クラスタ要約は失敗が続いても次の拍まで待って回す()
    {
        var clock = new ManualTickClock();
        var coordinator = new ThrowTwiceCoordinator(clock);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterSummaryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            Options.Create(new ClusterSummaryOptions { Enabled = true }),
            new RecordingLogger<ClusterSummaryHostedService>())
        { CycleInterval = TickCycle, CycleClock = clock };

        await AssertWaitsForNextTickAsync(worker, clock, coordinator);
    }

    [Fact]
    public async Task ナレッジ健全性の報告は失敗が続いても次の拍まで待って回す()
    {
        var clock = new ManualTickClock();
        var coordinator = new ThrowTwiceCoordinator(clock);
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new KnowledgeHealthHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            new RecordingLogger<KnowledgeHealthHostedService>())
        { CycleInterval = TickCycle, CycleClock = clock };

        await AssertWaitsForNextTickAsync(worker, clock, coordinator);
    }

    private static async Task AssertWaitsForNextTickAsync(
        BackgroundService worker, ManualTickClock clock, ThrowTwiceCoordinator coordinator)
    {
        var start = clock.GetUtcNow();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 拍の源が作られる前に進めた時刻は拍にならない（StartAsync は ExecuteAsync を待たずに返り得る）。
            await clock.TimerCreated.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            for (var tick = 1; tick <= 3; tick++)
            {
                // tick 1 の前: 初回は 1 周期後。tick 2・3 の前: 直前の取得は失敗している —— 拍を進めるまで次の取得は来ない。
                await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
                coordinator.Calls.Should().Be(tick - 1,
                    tick == 1 ? "初回の周期は 1 拍目を待つ" : $"{tick - 1} 回目の失敗の後、次の拍を進めるまで取得しない（待たずに再試行していない）");

                clock.Advance(TickCycle);
                (await coordinator.WaitForCallAsync(Deadline)).Should().BeTrue($"拍 {tick} で {tick} 回目の取得が起きる");
            }

            coordinator.AcquiredAt().Should().Equal(
                [start + TickCycle, start + 2 * TickCycle, start + 3 * TickCycle],
                "取得はそれぞれ別の拍で起きている（失敗の後に同じ拍のうちに再試行していない）");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertLoopSurvivesAsync(
        BackgroundService worker, ForeignCancellationCoordinator coordinator, Func<IReadOnlyList<Exception?>> errors)
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 直す前の形では 1 周期目でループが終わり、2 回目の取得は来ない（ここが時間切れで赤になる）。
            await coordinator.SecondAcquire.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            worker.ExecuteTask!.IsCompleted.Should().BeFalse("停止要求は出していない。ループは回り続けている");
            errors().Should().Contain(e => e is OperationCanceledException,
                "停止要求でない取り消しは周期の失敗として記録する（黙って読み飛ばさない）");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // 停止要求では従前どおり静かに終わる（例外で終わらない）。
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
    }

    // 1 回目の取得で**停止要求と無関係な**取り消し（HttpClient の時間切れと同じ型）を投げ、以後はリースを渡さない。
    private sealed class ForeignCancellationCoordinator
        : IClusterDetectionLeaseCoordinator, IClusterSummaryLeaseCoordinator, IKnowledgeHealthLeaseCoordinator
    {
        private int _calls;

        public TaskCompletionSource SecondAcquire { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromException<IAsyncDisposable?>(new TaskCanceledException("下流の時間切れ（停止要求ではない）"));
            SecondAcquire.TrySetResult();
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    // 1・2 回目の取得で投げ（停止要求と無関係な取り消しと、ふつうの例外を 1 回ずつ）、以後はリースを渡さない。
    // 各回が見た**偽の時計の時刻**を記録する（#1622。壁時計は測らない）。
    private sealed class ThrowTwiceCoordinator(TimeProvider clock)
        : IClusterDetectionLeaseCoordinator, IClusterSummaryLeaseCoordinator, IKnowledgeHealthLeaseCoordinator
    {
        private readonly ConcurrentQueue<DateTimeOffset> _acquiredAt = new();
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<DateTimeOffset> AcquiredAt() => [.. _acquiredAt];

        public Task<bool> WaitForCallAsync(TimeSpan deadline) =>
            _called.WaitAsync(deadline, TestContext.Current.CancellationToken);

        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            _acquiredAt.Enqueue(clock.GetUtcNow());
            var call = Interlocked.Increment(ref _calls);
            _called.Release();
            return call switch
            {
                1 => Task.FromException<IAsyncDisposable?>(new TaskCanceledException("下流の時間切れ（停止要求ではない）")),
                2 => Task.FromException<IAsyncDisposable?>(new InvalidOperationException("リースの取得に失敗")),
                _ => Task.FromResult<IAsyncDisposable?>(null),
            };
        }
    }

    // #1622: 周期の拍を試験が手で進める偽の時計。`PeriodicTimer` がこの時計から拍の源を作ったことを知らせる
    // （作られる前に進めた時刻は拍にならない —— 偽の時計の拍は、源が作られた時刻から数える）。
    private sealed class ManualTickClock : FakeTimeProvider
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<Exception?> _errors = new();

        public IReadOnlyList<Exception?> Errors() => [.. _errors];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) _errors.Enqueue(exception);
        }
    }
}
