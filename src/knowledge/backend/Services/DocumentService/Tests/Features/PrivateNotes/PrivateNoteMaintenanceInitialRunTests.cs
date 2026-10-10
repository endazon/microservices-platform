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
