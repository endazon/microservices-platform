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
