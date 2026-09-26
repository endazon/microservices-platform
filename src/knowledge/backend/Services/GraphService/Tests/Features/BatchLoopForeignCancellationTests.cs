using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Features.Clustering.Detect;
using GraphService.Features.Clustering.Summarize;
using GraphService.Features.KnowledgeHealth.Report;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    // 周期を 300 ミリ秒にし、1・2 回目のリースの取得で投げ、3 回目までの各回の間隔が周期の半分以上あることを測る。
    private static readonly TimeSpan TickCycle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task クラスタ検出は失敗が続いても次の拍まで待って回す()
    {
        var coordinator = new ThrowTwiceCoordinator();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterDetectionHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            new RecordingLogger<ClusterDetectionHostedService>())
        { CycleInterval = TickCycle };

        await AssertWaitsForNextTickAsync(worker, coordinator);
    }

    [Fact]
    public async Task クラスタ要約は失敗が続いても次の拍まで待って回す()
    {
        var coordinator = new ThrowTwiceCoordinator();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ClusterSummaryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            Options.Create(new ClusterSummaryOptions { Enabled = true }),
            new RecordingLogger<ClusterSummaryHostedService>())
        { CycleInterval = TickCycle };

        await AssertWaitsForNextTickAsync(worker, coordinator);
    }

    [Fact]
    public async Task ナレッジ健全性の報告は失敗が続いても次の拍まで待って回す()
    {
        var coordinator = new ThrowTwiceCoordinator();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new KnowledgeHealthHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), coordinator,
            new RecordingLogger<KnowledgeHealthHostedService>())
        { CycleInterval = TickCycle };

        await AssertWaitsForNextTickAsync(worker, coordinator);
    }

    private static async Task AssertWaitsForNextTickAsync(BackgroundService worker, ThrowTwiceCoordinator coordinator)
    {
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await coordinator.ThirdAcquire.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var at = coordinator.AcquiredAt();
            (at[1] - at[0]).Should().BeGreaterThanOrEqualTo(TickCycle / 2, "1 回目の失敗の後、次の拍まで待っている");
            (at[2] - at[1]).Should().BeGreaterThanOrEqualTo(TickCycle / 2, "2 回目の失敗の後も、次の拍まで待っている");
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

    // 1・2 回目の取得で投げ（停止要求と無関係な取り消しと、ふつうの例外を 1 回ずつ）、以後はリースを渡さない。各回の時刻を記録する。
    private sealed class ThrowTwiceCoordinator
        : IClusterDetectionLeaseCoordinator, IClusterSummaryLeaseCoordinator, IKnowledgeHealthLeaseCoordinator
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TimeSpan> _acquiredAt = new();
        private int _calls;

        public TaskCompletionSource ThirdAcquire { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<TimeSpan> AcquiredAt() => [.. _acquiredAt];

        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            _acquiredAt.Enqueue(_clock.Elapsed);
            switch (Interlocked.Increment(ref _calls))
            {
                case 1:
                    return Task.FromException<IAsyncDisposable?>(new TaskCanceledException("下流の時間切れ（停止要求ではない）"));
                case 2:
                    return Task.FromException<IAsyncDisposable?>(new InvalidOperationException("リースの取得に失敗"));
                case 3:
                    ThirdAcquire.TrySetResult();
                    break;
            }
            return Task.FromResult<IAsyncDisposable?>(null);
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
