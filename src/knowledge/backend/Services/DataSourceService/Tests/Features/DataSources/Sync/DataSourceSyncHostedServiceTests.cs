using System.Collections.Concurrent;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Infrastructure.Persistence;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources;
using DataSourceService.Features.DataSources.Sync;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-01, UC-04, IADR-0083 (#305): 定期同期ワーカーの単一書き手化ゲートを検証する。
// リースを取得できたレプリカのみが同期を実行し、取得できない周期はスキップする（本番マルチレプリカでの冗長 fetch 排除）。
// 各テストは独立した InMemory DB を持つ専用 factory を用いる（ワーカーは有効化するが、TryRunCycleAsync を直接 1 回だけ回す）。
[Trait("TestKind", "Integration")]
public sealed class DataSourceSyncHostedServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // リース取得成功 → 1 サイクルで active データソースを同期し watermark（LastSyncedAt）が前進、リースが解放される。
    [Fact]
    public async Task Cycle_WhenLeaseAcquired_RunsSync_AdvancesWatermark_AndReleasesLease()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient(); // ホスト（＝テストバス）を起動する。
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var id = await SeedActiveFilesystemSourceAsync(factory, "granted-share", dir);

        var lease = new RecordingLease();
        var worker = BuildWorker(factory, new GrantingCoordinator(lease));

        var ran = await worker.TryRunCycleAsync(CancellationToken.None);

        ran.Should().BeTrue("リースを取得できたレプリカは同期を実行する");
        (await ReloadLastSyncedAtAsync(factory, id))
            .Should().NotBeNull("完全成功で watermark が前進する（同期が実行された証跡）");
        lease.Disposed.Should().BeTrue("同期後にリースを解放する（次周期で他レプリカも取得できる）");
    }

    // リース取得失敗（他レプリカが実行中）→ 同期は実行されず watermark も前進しない（本サイクルはスキップ・fail-safe）。
    [Fact]
    public async Task Cycle_WhenLeaseDenied_SkipsSync_DoesNotAdvanceWatermark()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var dir = CreateTempDirWithFile("skip.md", "skip");
        var id = await SeedActiveFilesystemSourceAsync(factory, "denied-share", dir);

        var worker = BuildWorker(factory, new DenyingCoordinator());

        var ran = await worker.TryRunCycleAsync(CancellationToken.None);

        ran.Should().BeFalse("リースを取得できない周期はスキップする（他レプリカが単一書き手）");
        (await ReloadLastSyncedAtAsync(factory, id))
            .Should().BeNull("同期を実行しないため watermark は前進しない（冗長 fetch を出さない）");
    }

    // 🔴 FR-01, UC-04 基本 2, IADR-0083 の 2026-09-26 追記 (#1604): **定期同期のループは、停止要求ではない取り消し
    // （コネクタの接続の時間切れ等）で終わらない。** 失敗は記録し、**次の拍まで待って**から再び回す。
    //
    // 直す前は型だけの `catch (OperationCanceledException) { break; }` で、1 回の時間切れで定期同期が**黙って永久に**止まった
    // （ログなし・プロセスは健全）。周期は構成から来て最短 30 秒に丸められるので、試験だけが与える口 `CycleInterval` で短くする。
    // 1・2 回目のリースの取得で時間切れと同じ型を投げ、3 回目の取得が起きること、各回が**別の拍**で起きること
    // （＝失敗の後に待たずに再試行していない）を測る。
    //
    // ［#1622］IADR-0083 の追記: 拍は**偽の時計**（`CycleClock` に `FakeTimeProvider`）で試験が手で進める。従前は周期 300 ミリ秒の実時間で
    // 「各回の間隔が周期の半分以上」を測っており、負荷で本体が遅れると `PeriodicTimer` が溜まった拍をすぐに発火し、正しい実装でも落ち得た。
    // 判定: (a) 失敗の後、拍を進める前は次の取得が来ない（静穏の窓を置いて回数を見る）、(b) k 回目の取得が見た偽の時刻が「開始 + (k−1) 周期」
    // （本ワーカーは起動時に 1 回目を回す）。正しい実装では (a)(b) とも決定的に成り立つ。窓が効くのは M1（待たずに再試行）の側だけである。
    [Fact]
    public async Task Loop_SurvivesForeignCancellation_AndWaitsForTheNextTickAfterEachFailure()
    {
        var cycle = TimeSpan.FromHours(1);
        var quiet = TimeSpan.FromMilliseconds(250);
        var deadline = TimeSpan.FromSeconds(10);
        var clock = new ManualTickClock();
        var start = clock.GetUtcNow();
        var coordinator = new ThrowTwiceCoordinator(clock);
        var logger = new RecordingLogger<DataSourceSyncHostedService>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataSourceSyncHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            coordinator,
            new SyncSchedule(TimeProvider.System),
            Options.Create(new DataSourceSyncOptions { Enabled = true }),
            logger)
        { CycleInterval = cycle, CycleClock = clock };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 拍の源が作られる前に進めた時刻は拍にならない（StartAsync は ExecuteAsync を待たずに返り得る）。
            await clock.TimerCreated.WaitAsync(deadline, TestContext.Current.CancellationToken);
            (await coordinator.WaitForCallAsync(deadline)).Should().BeTrue("起動時に 1 回目の取得が起きる");
            for (var tick = 1; tick <= 2; tick++)
            {
                // 直す前の形では 1 回目の取得でループが終わり、2 回目は来ない（下の待ちが時間切れで赤になる）。
                await Task.Delay(quiet, TestContext.Current.CancellationToken);
                coordinator.Calls.Should().Be(tick,
                    $"{tick} 回目の失敗の後、次の拍を進めるまで取得しない（待たずに再試行していない）");

                clock.Advance(cycle);
                (await coordinator.WaitForCallAsync(deadline)).Should().BeTrue($"拍 {tick} で {tick + 1} 回目の取得が起きる");
            }

            worker.ExecuteTask!.IsCompleted.Should().BeFalse("停止要求は出していない。ループは回り続けている");
            logger.Errors().Where(e => e is OperationCanceledException).Should().HaveCount(2,
                "停止要求でない取り消しは周期の失敗として記録する（黙って読み飛ばさない）");
            coordinator.AcquiredAt().Should().Equal([start, start + cycle, start + 2 * cycle],
                "取得はそれぞれ別の拍で起きている（失敗の後に同じ拍のうちに再試行していない）");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
    }

    // FR-01, UC-04, IADR-0053, IADR-0054, IADR-0083 の 2026-09-26 追記 (#1604): Wiki / SaaS コネクタの名前付きクライアントは
    // **明示の期限**を持つ（既定の 100 秒のままだと、固まった接続先 1 つが定期同期の 1 周を 100 秒止める）。本番の Program.cs の DI から引く。
    [Fact]
    public void HttpConnectorClients_HaveAnExplicitTimeout()
    {
        using var factory = new TestWebApplicationFactory();
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();

        clients.CreateClient(WikiConnector.HttpClientName).Timeout.Should().Be(TimeSpan.FromSeconds(30));
        clients.CreateClient(SaaSConnector.HttpClientName).Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    private static DataSourceSyncHostedService BuildWorker(
        TestWebApplicationFactory factory, ISyncLeaseCoordinator coordinator) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            coordinator,
            // SC-06: 次回同期の位相（本テストの対象はリースのゲートなので、記録先を渡すだけ）。
            factory.Services.GetRequiredService<SyncSchedule>(),
            Options.Create(new DataSourceSyncOptions { Enabled = true }),
            NullLogger<DataSourceSyncHostedService>.Instance);

    private async Task<Guid> SeedActiveFilesystemSourceAsync(
        TestWebApplicationFactory factory, string name, string rootPath)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var ds = DataSource.Create(name, "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = rootPath });
        db.DataSources.Add(ds);
        await db.SaveChangesAsync();
        return ds.Id;
    }

    private static async Task<DateTimeOffset?> ReloadLastSyncedAtAsync(TestWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var ds = await db.DataSources.AsNoTracking().SingleAsync(d => d.Id == id);
        return ds.LastSyncedAt;
    }

    private string CreateTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kp-hs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        _tempDirs.Add(dir);
        return dir;
    }

    // 取得成功を返し、破棄されたかを記録するリース（解放されることの検証用）。
    private sealed class RecordingLease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrantingCoordinator(IAsyncDisposable lease) : ISyncLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(lease);
    }

    // 1・2 回目の取得で停止要求と無関係な取り消し（HttpClient の時間切れと同じ型）を投げ、以後はリースを渡さない
    // （＝本体は DB を読まない）。各回が見た**偽の時計の時刻**を記録する（#1622。壁時計は測らない）。
    private sealed class ThrowTwiceCoordinator(TimeProvider clock) : ISyncLeaseCoordinator
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
            if (call <= 2)
                return Task.FromException<IAsyncDisposable?>(new TaskCanceledException("接続の時間切れ（停止要求ではない）"));
            return Task.FromResult<IAsyncDisposable?>(null);
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

    private sealed class DenyingCoordinator : ISyncLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
