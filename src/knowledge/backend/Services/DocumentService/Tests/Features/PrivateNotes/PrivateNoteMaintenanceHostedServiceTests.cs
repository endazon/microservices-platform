using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
}
