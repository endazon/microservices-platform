using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, FR-22, ADR-0096 決定 1・2, [[IADR-0431]] の 2026-09-26 追記 (#1598):
// **個人資料の日次保守ループは、停止要求ではない取り消しで終わらない。**
//
// 🔴 直す前の形は、周期の本体の `catch (Exception) when (ex is not OperationCanceledException)` が取り消しを素通しし、
// 外側の型だけの `catch (OperationCanceledException)` が「シャットダウン」と読んでループを**永久に**終えていた。
// プロセスは生きたまま、退職者の資料の削除・90 日の削除・通知が以後 1 度も動かない（取り返しの効かない側ではないが、
// 計画の 30 日の窓を実装が黙って無期限に延ばす）。
//
// 周期は `CycleInterval`（試験だけが短くする口）で数十ミリ秒にする。本体は試験ホストのスコープの本物の
// `PrivateNoteMaintenanceService` で、1 周期目の 1 巡目の判定（捕まえない側）に停止要求と無関係な取り消しを投げさせ、
// 2 周期目で同じ所有者が「経過」と答えて資料が消えること＝**周期が回り続けている**ことを上限つきで待つ。
// 本クラスの DB（`TestWebApplicationFactory` はクラスごとに別）に居る所有者は本試験の 1 人だけである。
[Trait("TestKind", "Integration")]
public class PrivateNoteMaintenanceHostedServiceTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static readonly TimeSpan ShortCycle = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task 停止要求でない取り消しで周期が失敗しても次の周期で退職者の資料を削除する()
    {
        var user = $"loop-{Guid.NewGuid():N}"[..20];
        var noteId = await PushNoteAsync(await PluginAsync(user), "loop.md", "本文");
        factory.OwnerRetention.DeclareSequence(user,
            () => throw new TaskCanceledException("下流の時間切れ（停止要求ではない）"),
            StubOwnerRetentionDirectory.Departed,
            StubOwnerRetentionDirectory.Departed);
        var logger = new RecordingLogger<PrivateNoteMaintenanceHostedService>();
        var worker = new PrivateNoteMaintenanceHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), logger)
        { CycleInterval = ShortCycle };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 直す前の形では 1 周期目でループが終わり、資料は残り続ける（ここが時間切れで赤になる）。
            await WaitUntilDeletedAsync(noteId);
            worker.ExecuteTask!.IsCompleted.Should().BeFalse("停止要求は出していない。ループは回り続けている");
            logger.OfLevel(LogLevel.Error).Should().Contain(e => e.Exception is OperationCanceledException,
                "停止要求でない取り消しは周期の失敗として記録する（黙って読み飛ばさない）");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // 停止要求では従前どおり静かに終わる（例外で終わらない）。
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("停止要求はシャットダウンとして正常に終える");
    }

    // ［#1604］FR-19, FR-22, [[IADR-0431]] 決定 5 の追記: 失敗した周期の後は**次の拍まで待つ**（間を空けずに再試行しない）。
    // 上の試験は「次の周期が来る」ことしか測っておらず、失敗の直後に待たずに再試行する変異（M1）が生き残った（#1601 の監査）。
    // 1・2 周期目の 1 巡目の判定で投げ（停止要求と無関係な取り消しと、ふつうの例外）、3 周期目で「経過」と答えさせる。
    // 各回の判定が**別の拍**で起きることを測る。
    //
    // ［#1622］[[IADR-0431]] 決定 5 の追記: 拍は**偽の時計**（`CycleClock` に `FakeTimeProvider`）で試験が手で進める。従前は周期 300 ミリ秒の
    // 実時間で「判定の間隔が周期の半分以上」を測っており、1 周期目の本体（スコープ・DB 読み）が負荷で 300 ミリ秒を超えると `PeriodicTimer` が
    // 溜まった拍をすぐに発火し、**正しい実装でも**落ちた（監査で全体の実行 3 回中 2 回・develop でも再現）。偽の時計は試験が進めない限り進まない。
    // 判定: (a) 失敗の後、拍を進める前は次の判定が来ない（静穏の窓を置いて回数を見る）、(b) k 回目の判定が見た偽の時刻が「開始 + k 周期」。
    // 正しい実装では (a)(b) とも決定的に成り立つ（窓の長さに依らない）。窓が効くのは M1（待たずに再試行）の側だけである。
    [Fact]
    public async Task 周期の失敗が続いても次の拍まで待ってから再び判定する()
    {
        var cycle = TimeSpan.FromHours(1);
        var quiet = TimeSpan.FromMilliseconds(250);
        var user = $"tick-{Guid.NewGuid():N}"[..20];
        var noteId = await PushNoteAsync(await PluginAsync(user), "tick.md", "本文");
        var clock = new ManualTickClock();
        var start = clock.GetUtcNow();
        var askedAt = new ConcurrentQueue<DateTimeOffset>();
        using var asked = new SemaphoreSlim(0);
        OwnerRetentionStatus? Ask(Func<OwnerRetentionStatus?> answer)
        {
            askedAt.Enqueue(clock.GetUtcNow());
            asked.Release();
            return answer();
        }
        factory.OwnerRetention.DeclareSequence(user,
            () => Ask(() => throw new TaskCanceledException("下流の時間切れ（停止要求ではない）")),
            () => Ask(() => throw new InvalidOperationException("判定の口の一時障害")),
            () => Ask(StubOwnerRetentionDirectory.Departed),
            StubOwnerRetentionDirectory.Departed);
        var worker = new PrivateNoteMaintenanceHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<PrivateNoteMaintenanceHostedService>())
        { CycleInterval = cycle, CycleClock = clock };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 拍の源が作られる前に進めた時刻は拍にならない（StartAsync は ExecuteAsync を待たずに返り得る）。
            await clock.TimerCreated.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            for (var tick = 1; tick <= 3; tick++)
            {
                // tick 1 の前: 初回は 1 周期後。tick 2・3 の前: 直前の判定は失敗している —— 拍を進めるまで次の判定は来ない。
                await Task.Delay(quiet, TestContext.Current.CancellationToken);
                askedAt.Should().HaveCount(tick - 1,
                    tick == 1 ? "初回の周期は 1 拍目を待つ" : $"{tick - 1} 回目の失敗の後、次の拍を進めるまで判定しない（待たずに再試行していない）");

                clock.Advance(cycle);
                (await asked.WaitAsync(Deadline, TestContext.Current.CancellationToken))
                    .Should().BeTrue($"拍 {tick} で {tick} 回目の判定が起きる");
            }
            await WaitUntilDeletedAsync(noteId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        askedAt.Should().Equal([start + cycle, start + 2 * cycle, start + 3 * cycle],
            "判定はそれぞれ別の拍で起きている（失敗の後に同じ拍のうちに再試行していない）");
    }

    private async Task WaitUntilDeletedAsync(Guid noteId)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
                if (!await db.PrivateNotes.AnyAsync(n => n.DocumentId == noteId, TestContext.Current.CancellationToken))
                    return;
            }
            await Task.Delay(ShortCycle, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"{Deadline.TotalSeconds} 秒待っても 2 周期目が退職者の資料を消さなかった（1 周期目の取り消しでループが終わっている）");
    }

    // 同期トークンを発行し、本文つきの資料を作れる状態にする（`PrivateNoteDepartedOwnerPurgeTests` と同じ器）。
    private async Task<HttpClient> PluginAsync(string user)
    {
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return plugin;
    }

    private static async Task<Guid> PushNoteAsync(HttpClient plugin, string path, string content)
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = path,
            title = path,
            edits = new[] { new { content } },
        }, TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken))!.NoteId;
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
}
