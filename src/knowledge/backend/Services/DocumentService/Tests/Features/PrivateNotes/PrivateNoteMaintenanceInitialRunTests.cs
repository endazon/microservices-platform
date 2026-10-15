using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.ExternalServices;
using DocumentService.Tests.Grpc;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace DocumentService.Tests.Features.PrivateNotes;

// NFR-16, FR-22, ADR-0117, ADR-0037 決定 6, [[IADR-0530]] 決定 3 (#1887):
// **計測専用の前倒し**（`PrivateNotes:Maintenance:InitialRunDelaySeconds`）が、
//   ① 既定（未設定）では効かない（初回は従前どおり 1 周期後）
//   ② 設定したときだけ、起動の N 秒後に**本物の周期**を 1 回走らせ、論理削除済みの資料の所有者へ週次の通知（①-a）が出る
//   ③ その通知が **本番の gRPC 実装（`GrpcPrivateNoteNotifier`）で `NotificationIngress/Accept` まで届く**（N-1 の経路そのもの）
//   ④ 不正な値は起動を止める（黙って既定へ倒すと、計測者は前倒しが効いたと思ったまま 24 時間待つ）
// ことを固定する。
//
// ［2026-10-15 追記 / #1890］#1889 の独立監査で生き残った 3 つの変異を殺す試験を足した。
//   M4: `Program.cs` の配線（`InitialRunDelay = ResolveInitialRunDelay(...)`）を外す → 鍵を設定した**ホスト**で測る（⑤）
//   M5: 前倒しの 1 回の後に 24 時間周期のループへ進まない → 偽の時計で「前倒し + 24 時間」進めて 2 回目を待つ（⑥）
//   M6: 前倒しの待ち（`Task.Delay`）が停止要求を無視する → 待ちの最中に停止を要求し、速やかに終わることを測る（⑦）
// 周期の回数は、周期ごとに 1 つ作られるスコープの数で数える（`CountingScopeFactory`。中身は試験ホストの本物のスコープ）。
[Trait("TestKind", "Integration")]
public class PrivateNoteMaintenanceInitialRunTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ④ 構成の読み方。未設定・空白は null（従前どおり）、1〜86400 の整数だけを採る。
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    [InlineData("86400", 86400)]
    public void 前倒しの秒数を構成から読む(string? raw, int? expectedSeconds)
    {
        var resolved = PrivateNoteMaintenanceHostedService.ResolveInitialRunDelay(Config(raw));

        resolved.Should().Be(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("86401")]
    [InlineData("1.5")]
    [InlineData("120s")]
    [InlineData("abc")]
    public void 不正な前倒しの値は起動を止める(string raw)
    {
        var act = () => PrivateNoteMaintenanceHostedService.ResolveInitialRunDelay(Config(raw));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{PrivateNoteMaintenanceHostedService.InitialRunDelayKey}*");
    }

    // ① 本番の組み立て（Program.cs）は、既定の構成では前倒しを入れない。
    [Fact]
    public void 既定の構成では前倒しは入らない()
    {
        var hosted = factory.Services.GetServices<IHostedService>()
            .OfType<PrivateNoteMaintenanceHostedService>()
            .Should().ContainSingle("日次の保守は 1 つだけ登録されている").Subject;

        hosted.InitialRunDelay.Should().BeNull("未設定なら初回は従前どおり起動の 1 周期（24 時間）後");
    }

    // ② 前倒しを設定したときだけ、起動の N 秒後に本物の周期が 1 回走り、週次の通知（①-a）が出る。
    // 拍は偽の時計で試験が手で進める（`PrivateNoteMaintenanceHostedServiceTests` と同じ器）。
    [Fact]
    public async Task 前倒しを設定すると起動のN秒後に1回走り週次の通知が出る()
    {
        var user = $"early-{Guid.NewGuid():N}"[..20];
        await CreateSoftDeletedNoteAsync(user);
        var clock = new ManualTickClock();
        var delay = TimeSpan.FromMinutes(2);
        var worker = new PrivateNoteMaintenanceHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        { CycleInterval = PrivateNoteMaintenanceHostedService.Interval, CycleClock = clock, InitialRunDelay = delay };

        await worker.StartAsync(Ct);
        try
        {
            await clock.TimerCreated.WaitAsync(Deadline, Ct);
            // 陰: 前倒しの秒数が経つまでは走らない。
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            WeeklyFor(user).Should().BeEmpty("前倒しの秒数が経つ前には周期は走らない");

            // 陽: 前倒しの秒数を進めると、1 周期分が走る（24 時間は待たない）。
            clock.Advance(delay);
            await WaitUntilAsync(() => WeeklyFor(user).Count > 0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        WeeklyFor(user).Should().ContainSingle()
            .Which.Count.Should().Be(1, "論理削除済みの資料 1 件の件数だけを運ぶ（タイトルを運ばない）");
    }

    // ⑤ (#1890 M4) 本番の組み立て（Program.cs）が構成の鍵を読む: 不正な値を入れたホストは起動（StartAsync）で止まる。
    // 配線を外すと鍵は読まれず、ホストは黙って起動する（＝前倒しが効かないまま計測者が 24 時間待つ）。
    [Fact]
    public void 鍵を不正な値にしたホストは起動で止まる()
    {
        var host = WithInitialRunDelay("abc");
        try
        {
            var act = () => host.Services; // ホストの組み立てと起動（ホスト型サービスの解決は StartAsync の中）

            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*{PrivateNoteMaintenanceHostedService.InitialRunDelayKey}*'abc'*");
        }
        finally
        {
            DisposeQuietly(host);
        }
    }

    // ⑤ の陽性対照: 正しい値を入れたホストでは、登録された保守がその前倒しを持つ。
    [Fact]
    public void 鍵を設定したホストの保守は前倒しを持つ()
    {
        var host = WithInitialRunDelay("120");
        try
        {
            var hosted = host.Services.GetServices<IHostedService>()
                .OfType<PrivateNoteMaintenanceHostedService>()
                .Should().ContainSingle().Subject;

            hosted.InitialRunDelay.Should().Be(TimeSpan.FromSeconds(120), "構成の鍵が本番の組み立てで前倒しへ渡る");
        }
        finally
        {
            DisposeQuietly(host);
        }
    }

    // ⑥ (#1890 M5) 前倒しの 1 回の後も、24 時間ごとの周期へ進む（前倒しは 1 回目の時刻を変えるだけ）。
    // 陰: 前倒しの後、24 時間に 1 tick 足りないところでは 2 回目は来ない（前倒しの直後に周期を重ねない）。
    [Fact]
    public async Task 前倒しの後も24時間ごとの周期へ進む()
    {
        var clock = new TimerCountingClock();
        var scopes = new CountingScopeFactory(factory.Services.GetRequiredService<IServiceScopeFactory>());
        var delay = TimeSpan.FromMinutes(2);
        var worker = new PrivateNoteMaintenanceHostedService(scopes, new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        { CycleInterval = PrivateNoteMaintenanceHostedService.Interval, CycleClock = clock, InitialRunDelay = delay };

        await worker.StartAsync(Ct);
        try
        {
            await clock.WaitForTimersAsync(1); // 前倒しの待ち
            clock.Advance(delay);
            await scopes.WaitForCyclesAsync(1);

            await clock.WaitForTimersAsync(2); // 24 時間周期の拍の源。変異 M5 ではここが作られず時間切れになる
            clock.Advance(PrivateNoteMaintenanceHostedService.Interval - TimeSpan.FromTicks(1));
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            scopes.Cycles.Should().Be(1, "前倒しの後、24 時間が経つまでは 2 回目は走らない");

            clock.Advance(TimeSpan.FromTicks(1));
            await scopes.WaitForCyclesAsync(2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
    }

    // ⑦ (#1890 M6) 前倒しの待ちの最中に停止を要求すると、時計を進めなくても速やかに終わり、周期は走らない。
    // 偽の時計は試験が進めない限り進まない —— 終われるのは待ちが停止要求を見ているときだけである。
    [Fact]
    public async Task 前倒しの待ちの最中に停止を要求すると速やかに終わる()
    {
        var clock = new TimerCountingClock();
        var scopes = new CountingScopeFactory(factory.Services.GetRequiredService<IServiceScopeFactory>());
        var worker = new PrivateNoteMaintenanceHostedService(scopes, new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        { CycleInterval = PrivateNoteMaintenanceHostedService.Interval, CycleClock = clock, InitialRunDelay = TimeSpan.FromHours(1) };

        await worker.StartAsync(Ct);
        await clock.WaitForTimersAsync(1); // 前倒しの待ちに入った

        // 変異 M6 では停止要求が待ちに届かず、ここが時間切れ（TimeoutException）になる。
        await worker.StopAsync(CancellationToken.None).WaitAsync(Deadline, Ct);

        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
        scopes.Cycles.Should().Be(0, "停止の要求で前倒しの周期を走らせない");
        clock.TimersCreated.Should().Be(1, "前倒しの待ちから周期のループへ進まずに終わる");
    }

    // ③ N-1 の経路そのもの: 前倒しで走る周期の通知は、本番の gRPC 実装で `NotificationIngress/Accept` まで届く。
    // 受け口は 127.0.0.1 の実 gRPC サーバー（h2c）に載せた偽物で、チャネルと生成クライアントは本番と同じ形である。
    [Fact]
    public async Task 周期の通知は本番のgRPC実装でNotificationIngressのAcceptへ届く()
    {
        var user = $"grpc-{Guid.NewGuid():N}"[..20];
        await CreateSoftDeletedNoteAsync(user);
        var ingress = new RecordingAcceptService();
        await using var server = await LoopbackGrpcServer.StartAsync(ingress, Ct);
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var notifier = new GrpcPrivateNoteNotifier(
            new Pb.NotificationIngress.NotificationIngressClient(server.Channel),
            new PrivateNoteNotificationMetrics(meters.GetRequiredService<IMeterFactory>()),
            TimeProvider.System,
            new RecordingLogger<GrpcPrivateNoteNotifier>());

        using (var scope = factory.Services.CreateScope())
        {
            // 本番の組み立てと同じ依存で、通知の実装だけを gRPC 版にする。
            var maintenance = ActivatorUtilities.CreateInstance<PrivateNoteMaintenanceService>(
                scope.ServiceProvider, (IPrivateNoteNotifier)notifier);
            await maintenance.RunAsync(DateTimeOffset.UtcNow, Ct);
        }

        ingress.Requests.Should().Contain(r =>
            r.Subject == user && r.Kind == PrivateNoteNotificationKinds.PrivateNotePurgeWeekly && r.Count == 1,
            "週次の通知（①-a）が gRPC の Accept で受け口へ届く（document → notification の N-1）");
    }

    private List<RecordingPrivateNoteNotifier.Sent> WeeklyFor(string user) =>
        factory.Notifier.OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeWeekly)
            .Where(n => n.Subject == user).ToList();

    // 画面（SC-19）と同じ口で、本文なしの資料を作って論理削除する（週次の通知の材料）。
    private async Task CreateSoftDeletedNoteAsync(string user)
    {
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        var created = await session.PostAsJsonAsync("/private-notes/", new { title = "計測用" }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = (await created.Content.ReadFromJsonAsync<PrivateNoteDto>(Ct))!;
        var deleted = await session.DeleteAsync($"/private-notes/{note.Id}", Ct);
        deleted.IsSuccessStatusCode.Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
        Assert.Fail($"{Deadline.TotalSeconds} 秒待っても前倒しの周期が走らなかった");
    }

    private static IConfiguration Config(string? raw) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PrivateNoteMaintenanceHostedService.InitialRunDelayKey] = raw,
        }).Build();

    // #1890: 鍵を入れた試験ホスト（器はこのクラスの fixture から派生させる。DB 名も同じ）。
    private WebApplicationFactory<Program> WithInitialRunDelay(string raw) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PrivateNoteMaintenanceHostedService.InitialRunDelayKey] = raw,
            })));

    // 起動に失敗したホストの後始末は、起動の例外とは別に失敗し得る（測りたいのは起動の側だけ）。
    private static void DisposeQuietly(IDisposable host)
    {
        try { host.Dispose(); }
        catch (Exception) { /* 起動に失敗したホストの後始末の失敗は測らない */ }
    }

    // #1890: 周期ごとに 1 つ作られるスコープを数える（中身は試験ホストの本物のスコープ）。
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private readonly SemaphoreSlim _created = new(0);
        private int _cycles;

        public int Cycles => Volatile.Read(ref _cycles);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _cycles);
            _created.Release();
            return inner.CreateScope();
        }

        public async Task WaitForCyclesAsync(int count)
        {
            while (Cycles < count)
                if (!await _created.WaitAsync(Deadline, Ct))
                    Assert.Fail($"{Deadline.TotalSeconds} 秒待っても {count} 回目の周期が走らなかった（走ったのは {Cycles} 回）");
        }
    }

    // #1890: 偽の時計から作られた拍の源（`Task.Delay` の待ち・`PeriodicTimer`）を数える。
    // 作られる前に進めた時刻は拍にならないので、試験は源が作られたのを見てから進める。
    private sealed class TimerCountingClock : FakeTimeProvider
    {
        private readonly SemaphoreSlim _created = new(0);
        private int _timers;

        public int TimersCreated => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            _created.Release();
            return timer;
        }

        public async Task WaitForTimersAsync(int count)
        {
            while (TimersCreated < count)
                if (!await _created.WaitAsync(Deadline, Ct))
                    Assert.Fail($"{Deadline.TotalSeconds} 秒待っても拍の源が {count} 個作られなかった（作られたのは {TimersCreated} 個）");
        }
    }

    // 受け口の偽物。受け取った要求を記録して受理する。
    private sealed class RecordingAcceptService : Pb.NotificationIngress.NotificationIngressBase
    {
        public ConcurrentQueue<Pb.AcceptRequest> Requests { get; } = new();

        public override Task<Pb.AcceptResponse> Accept(Pb.AcceptRequest request, ServerCallContext context)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new Pb.AcceptResponse());
        }
    }

    // 拍の源（タイマー）が作られたことを知らせる偽の時計（作られる前に進めた時刻は拍にならない）。
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
}
