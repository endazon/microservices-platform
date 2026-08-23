using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Endpoints;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using DocumentService.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Api.Tests;

// FR-19, UC-11, SC-19, ADR-0037 決定 5・6・16・20, [[IADR-0270]] 決定 2・6:
// 個人資料のライフサイクル（作成→論理削除→復元／完全削除）・90 日の自動物理削除・
// 3 段通知（週次／7 日前／事後）・版履歴の保持上限（直近 50 版かつ 90 日）。
public class PrivateNoteLifecycleTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private HttpClient PluginWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync()
    {
        var user = $"life-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" });
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>())!.Token;
        return (user, session, PluginWith(token));
    }

    private async Task<Guid> PushNoteAsync(HttpClient plugin, string path, string content)
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = path,
            title = path,
            edits = new[] { new { content } },
        });
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>())!.NoteId;
    }

    private async Task RunMaintenanceAsync(DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>()
            .RunAsync(now);
    }

    private async Task BackdateDeletionAsync(Guid noteId, DateTimeOffset deletedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var note = await db.PrivateNotes.FindAsync(noteId);
        note!.SoftDelete(deletedAt);
        await db.SaveChangesAsync();
    }

    // FR-19: SC-19 からの作成は本文なし・既定値つき（本文編集は Obsidian 経路に限る）。
    [Fact]
    public async Task SC19からの作成は本文なしで既定値つきで作られる()
    {
        var (user, session, _) = await OwnerAsync();
        var resp = await session.PostAsJsonAsync("/private-notes/", new { title = "空メモ" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await resp.Content.ReadFromJsonAsync<PrivateNoteDto>();
        note!.Bytes.Should().Be(0);
        note.Deleted.Should().BeFalse();
        note.IncludeInSearch.Should().BeFalse();

        // Document 側の既定値（doc_scope / owner / restricted）
        var doc = await factory.CreateClient().GetFromJsonAsync<Knowledge.Contracts.Dtos.DocumentDto>(
            $"/documents/{note.Id}");
        doc!.Attributes.Should().Contain("doc_scope", "private-note");
        doc.Attributes.Should().Contain("owner", user);
        doc.Attributes.Should().Contain("confidentiality", "restricted");
    }

    // FR-19（否定形＋陽性対照）: 他者の資料は SC-19 の操作からも到達できない（存在秘匿の 404）。
    [Fact]
    public async Task 他者の資料はSC19の削除復元完全削除から到達できない()
    {
        var (_, victimSession, victimPlugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(victimPlugin, "victim.md", "secret");
        var mallory = SessionAs($"mallory-{Guid.NewGuid():N}"[..20]);

        (await mallory.DeleteAsync($"/private-notes/{noteId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await mallory.PostAsync($"/private-notes/{noteId}/restore", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await mallory.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { noteId } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mallory.PutAsJsonAsync($"/private-notes/{noteId}/exposure",
            new { includeInSearch = true, includeInGraph = true, includeInAi = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 陽性対照: 所有者は削除できる
        (await victimSession.DeleteAsync($"/private-notes/{noteId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // FR-19: 論理削除は復元でき、完全削除の対象は削除済みのみ（アクティブな資料は purge できない）。
    [Fact]
    public async Task 論理削除は復元でき完全削除は削除済みのみが対象になる()
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "restore.md", "内容");

        // アクティブなまま purge → 409（論理削除を飛ばした完全削除はさせない）
        (await session.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { noteId } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await session.DeleteAsync($"/private-notes/{noteId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.PostAsync($"/private-notes/{noteId}/restore", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // 復元後は削除状態が消えている
        var list = await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/");
        list!.Notes.Single(n => n.Id == noteId).Deleted.Should().BeFalse();

        // 復元済みへの restore は 409（冪等ではなく状態エラーとして知らせる）
        (await session.PostAsync($"/private-notes/{noteId}/restore", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    // FR-19 受け入れ基準, ADR-0037 決定 5・6-③: 90 日経過した資料は自動的に物理削除され（復元不可）、
    // 事後通知（件数のみ）が届く。90 日未満の資料は消えない（陽性対照）。
    [Fact]
    public async Task 論理削除から90日経過で自動物理削除され事後通知が発火する()
    {
        var (user, session, plugin) = await OwnerAsync();
        var oldNote = await PushNoteAsync(plugin, "old.md", "古い");
        var freshNote = await PushNoteAsync(plugin, "fresh.md", "新しい");
        var now = DateTimeOffset.UtcNow;

        await BackdateDeletionAsync(oldNote, now.AddDays(-91));   // purge 期限超過
        await BackdateDeletionAsync(freshNote, now.AddDays(-30)); // まだ 60 日残っている

        await RunMaintenanceAsync(now);

        var list = await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/");
        list!.Notes.Should().NotContain(n => n.Id == oldNote, "90 日経過で物理削除される");
        list.Notes.Should().Contain(n => n.Id == freshNote && n.Deleted,
            "90 日未満は保管が続く（陽性対照）");

        // 文書実体も消えている（復元不可）
        (await factory.CreateClient().GetAsync($"/documents/{oldNote}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // FR-22 ①-c: 事後通知は件数のみ
        var done = factory.Notifier.OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeDone)
            .Where(n => n.Subject == user).ToList();
        done.Should().ContainSingle();
        done[0].Count.Should().Be(1);
    }

    // FR-22 ①-b, ADR-0037 決定 6-②: 完全削除の 7 日前に別建ての通知が 1 回だけ検知される。
    [Fact]
    public async Task 完全削除の7日前通知は1回だけ検知される()
    {
        var (user, _, plugin) = await OwnerAsync();
        var soonNote = await PushNoteAsync(plugin, "soon.md", "もうすぐ");
        var laterNote = await PushNoteAsync(plugin, "later.md", "まだ先");
        var now = DateTimeOffset.UtcNow;

        await BackdateDeletionAsync(soonNote, now.AddDays(-85));  // 残り 5 日（窓内）
        await BackdateDeletionAsync(laterNote, now.AddDays(-10)); // 残り 80 日（窓外）

        await RunMaintenanceAsync(now);
        var imminent = factory.Notifier
            .OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeImminent)
            .Where(n => n.Subject == user).ToList();
        imminent.Should().ContainSingle();
        imminent[0].Count.Should().Be(1, "窓外の資料は数えない");
        imminent[0].Deadline.Should().NotBeNull();

        // 再実行しても重複しない
        await RunMaintenanceAsync(now.AddHours(2));
        factory.Notifier.OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeImminent)
            .Where(n => n.Subject == user).Should().HaveCount(1);
    }

    // FR-22 ①-a, ADR-0037 決定 6-①: 週次通知は件数と最短期限のみを運び、7 日間隔を守る。
    [Fact]
    public async Task 週次の削除通知は7日間隔で件数と期限のみを運ぶ()
    {
        var (user, _, plugin) = await OwnerAsync();
        var note = await PushNoteAsync(plugin, "weekly.md", "対象");
        var now = DateTimeOffset.UtcNow;
        await BackdateDeletionAsync(note, now.AddDays(-10));

        await RunMaintenanceAsync(now);
        var weekly = factory.Notifier
            .OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeWeekly)
            .Where(n => n.Subject == user).ToList();
        weekly.Should().ContainSingle();
        weekly[0].Count.Should().Be(1);
        weekly[0].Deadline.Should().NotBeNull("完全削除までの残り日数を週次通知に含める");

        // 翌日はまだ送らない（週次）／ 8 日後には再送する
        await RunMaintenanceAsync(now.AddDays(1));
        factory.Notifier.OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeWeekly)
            .Where(n => n.Subject == user).Should().HaveCount(1);
        await RunMaintenanceAsync(now.AddDays(8));
        factory.Notifier.OfKind(PrivateNoteNotificationKinds.PrivateNotePurgeWeekly)
            .Where(n => n.Subject == user).Should().HaveCount(2);
    }

    // FR-19 受け入れ基準, ADR-0037 決定 16: 版履歴は「直近 50 版かつ 90 日」の**両方を満たさなく
    // なった版**だけが古い順に消える。片方だけでは消えない（両側の陽性対照つき）。
    [Fact]
    public async Task 版履歴は直近50版かつ90日の両方を外れた版だけが消える()
    {
        var (_, _, plugin) = await OwnerAsync();
        var now = DateTimeOffset.UtcNow;

        // 60 版を作る（1 push に 60 編集 = 60 版）
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = "sixty.md",
            title = "六十版",
            edits = Enumerable.Range(1, 60).Select(i => new { content = $"v{i}" }).ToList(),
        });
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var noteId = (await push.Content.ReadFromJsonAsync<PushNoteResponse>())!.NoteId;

        // ケース 1: 全 60 版が 90 日以内 → 50 版を超えても 1 版も消えない（日数条件の陽性対照）
        await RunMaintenanceAsync(now);
        (await CountVersionsAsync(noteId)).Should().Be(60,
            "作成から 90 日以内の版は 50 版を超えても残す");

        // ケース 2: 古い 20 版だけを 91 日前へ倒す → 直近 50 版から外れ、かつ 90 日超の
        // 版 1〜10 だけが消える（版 11〜20 は 90 日超だが直近 50 版以内なので残る＝版数条件の陽性対照）
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var versions = await db.DocumentVersions
                .Where(v => v.DocumentId == noteId)
                .OrderBy(v => v.Version).Take(20).ToListAsync();
            foreach (var v in versions)
                db.Entry(v).Property(x => x.CreatedAt).CurrentValue = now.AddDays(-91);
            await db.SaveChangesAsync();
        }

        await RunMaintenanceAsync(now);
        (await CountVersionsAsync(noteId)).Should().Be(50);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var remaining = await db.DocumentVersions.Where(v => v.DocumentId == noteId)
                .Select(v => v.Version).OrderBy(v => v).ToListAsync();
            remaining.First().Should().Be(11, "古い順に消える（版 1〜10 が落ち、11〜20 は直近 50 版以内なので残る）");
            remaining.Last().Should().Be(60);
        }
    }

    private async Task<int> CountVersionsAsync(Guid noteId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.DocumentVersions.CountAsync(v => v.DocumentId == noteId);
    }

    // FR-19, ADR-0037 決定 20: 一括の完全削除。解放される容量の合計が返り、監査は件数のみ。
    [Fact]
    public async Task 一括完全削除は合計の解放容量を返し監査は件数のみ残る()
    {
        var (user, session, plugin) = await OwnerAsync();
        var n1 = await PushNoteAsync(plugin, "b1.md", new string('a', 100));
        var n2 = await PushNoteAsync(plugin, "b2.md", new string('b', 200));
        (await session.DeleteAsync($"/private-notes/{n1}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.DeleteAsync($"/private-notes/{n2}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var purge = await session.PostAsJsonAsync("/private-notes/purge",
            new { ids = new[] { n1, n2 } });
        purge.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await purge.Content.ReadFromJsonAsync<PurgePrivateNotesResponse>();
        result!.PurgedCount.Should().Be(2);
        result.FreedBytes.Should().Be(300);

        var audit = factory.Audit.OfAction("private-note.purge")
            .Where(e => e.Subject == user).ToList();
        audit.Should().ContainSingle();
        audit[0].Detail.Should().Contain("count=2");
    }
}
