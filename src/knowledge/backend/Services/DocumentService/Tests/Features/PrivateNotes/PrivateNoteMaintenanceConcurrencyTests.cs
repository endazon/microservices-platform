using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Features.Documents;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.PrivateNotes;

// 🔴 FR-19, FR-02, IADR-0637 (#2150 段 1a): **定期処理の完全削除は所有者ごとに確定し、1 人分が並行する確定に負けても他は進む。**
//
// 文書行の削除は並行トークン（版・発行の連番）で照合される。1a の後は、Touch しない状態の変化（復元・共有の変更）や
// タグの改名・再発行にも負ける。周期の catch は `RunCycleAsync` にしか無いので、全件を 1 回で確定すると、1 件の負けで
// 他の所有者の削除と周期の後段（版の整理・通知）が止まる。ここでは、負けた所有者の分だけが残り、次周期で消えることを測る。
[Trait("TestKind", "Integration")]
public class PrivateNoteMaintenanceConcurrencyTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // 露出 ON の個人資料を 1 件作る（勝つ側の発行が連番を進めるよう、露出は ON にしておく）。
    private static async Task<(string Owner, Guid NoteId)> ExposedNoteAsync(WebApplicationFactory<Program> host)
    {
        var owner = $"mcc-{Guid.NewGuid():N}"[..20];
        var session = host.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = host.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = "mcc.md", title = "mcc.md", edits = new[] { new { content = "本文" } } }, Ct);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var noteId = (await push.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!.NoteId;
        (await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure", new UpdateExposureRequest(true, false, false), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (owner, noteId);
    }

    // 90 日より前に論理削除したことにする（`PurgeAt <= now` にする）。
    private static async Task ExpireAsync(WebApplicationFactory<Program> host, Guid noteId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var note = await db.PrivateNotes.SingleAsync(n => n.DocumentId == noteId, Ct);
        note.SoftDelete(Now.AddDays(-200));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task RunMaintenanceAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>().RunAsync(Now, Ct);
    }

    private static bool Exists(WebApplicationFactory<Program> host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.Any(d => d.Id == id);
    }

    private static bool DeletedPublished(WebApplicationFactory<Program> host, Guid id) =>
        host.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentDeleted>().Any(e => e.DocumentId == id);

    [Fact]
    public async Task 期限切れの削除は1人分が並行する確定に負けても他の所有者は消え_負けた分は次周期で消える()
    {
        var target = new RaceTarget();
        using var racing = DocumentRace.Host(factory, target);
        var (_, lostNote) = await ExposedNoteAsync(racing);
        var (_, keptGoing) = await ExposedNoteAsync(racing);
        await ExpireAsync(racing, lostNote);
        await ExpireAsync(racing, keptGoing);

        target.Id = lostNote;
        var run = () => RunMaintenanceAsync(racing);
        await run.Should().NotThrowAsync("1 人分の負けを周期の失敗にしない（後段の版の整理・通知を止めない）");

        target.Raced.Should().Be(1, "前提: 負ける側の確定の直前に、同じ資料への別の確定が入った");
        Exists(racing, keptGoing).Should().BeFalse("負けなかった所有者の資料は消える");
        DeletedPublished(racing, keptGoing).Should().BeTrue();
        Exists(racing, lostNote).Should().BeTrue("負けた所有者の分は行ごと巻き戻る");
        DeletedPublished(racing, lostNote).Should().BeFalse("負けた確定の発行は出ない");

        await RunMaintenanceAsync(racing);
        Exists(racing, lostNote).Should().BeFalse("次周期の再入で消える");
        DeletedPublished(racing, lostNote).Should().BeTrue();
    }

    [Fact]
    public async Task 退職者の削除は1人分が並行する確定に負けても他の退職者は消え_負けた分は次周期で消える()
    {
        var target = new RaceTarget();
        using var racing = DocumentRace.Host(factory, target);
        var (lostOwner, lostNote) = await ExposedNoteAsync(racing);
        var (otherOwner, otherNote) = await ExposedNoteAsync(racing);
        factory.OwnerRetention.DeclareDeparted(lostOwner);
        factory.OwnerRetention.DeclareDeparted(otherOwner);

        target.Id = lostNote;
        var run = () => RunMaintenanceAsync(racing);
        await run.Should().NotThrowAsync("1 人分の負けを周期の失敗にしない");

        target.Raced.Should().Be(1);
        Exists(racing, otherNote).Should().BeFalse("負けなかった退職者の資料は消える");
        Exists(racing, lostNote).Should().BeTrue("負けた退職者の分は行ごと巻き戻る");
        DeletedPublished(racing, lostNote).Should().BeFalse();

        await RunMaintenanceAsync(racing);
        Exists(racing, lostNote).Should().BeFalse("次周期の再入で消える");
    }
}
