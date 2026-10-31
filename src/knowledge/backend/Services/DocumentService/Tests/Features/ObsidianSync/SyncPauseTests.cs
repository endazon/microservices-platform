using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.ObsidianSync;

// FR-20, UC-11, SC-20 主要素 7, 計画 ADR-0145 決定 1〜4, [[IADR-0667]] (#2378): **同期の一時停止（サーバの側）。**
//
// 🔴 否定形（停止中は拒む）は必ず陽性対照（停止の前・再開の後に同じトークンで通る）と対にする ——
// 「常に拒む実装」でも否定形だけなら緑になる。
// 🔴 停止中の拒否は 401 と見分けられること（決定 2）を、状態コードと理由コードの両方で測る。
[Trait("TestKind", "Integration")]
public class SyncPauseTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> DeviceAsync(HttpClient session, string name)
    {
        var resp = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = name }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return PluginWith((await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token);
    }

    private static object PushBody(string path, string content, Guid? noteId = null, int? baseVersion = null)
        => new { noteId, baseVersion, vaultPath = path, title = "メモ", edits = new[] { new { content } } };

    private async Task<PushNoteResponse> PushNewAsync(HttpClient plugin, string path)
    {
        var resp = await plugin.PostAsJsonAsync("/private-notes/sync/notes", PushBody(path, "本文"), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!;
    }

    private static Task<HttpResponseMessage> PauseAsync(HttpClient session)
        => session.PostAsync("/private-notes/sync-settings/pause", null, Ct);

    private static Task<HttpResponseMessage> ResumeAsync(HttpClient session)
        => session.PostAsync("/private-notes/sync-settings/resume", null, Ct);

    /// <summary>同期トークンで受ける 5 つの口（ADR-0145 決定 2）。どれも資料を変えない要求の形にする（陽性対照で通っても副作用が小さい）。</summary>
    private static IReadOnlyList<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Send)> FiveRoutes(
        Guid noteId, int version, string path) =>
    [
        ("manifest", p => p.GetAsync("/private-notes/sync/manifest", Ct)),
        ("pull", p => p.GetAsync($"/private-notes/sync/notes/{noteId}", Ct)),
        ("push", p => p.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody(path, "本文"), Ct)),
        ("move", p => p.PostAsJsonAsync($"/private-notes/sync/notes/{noteId}/move",
            new { vaultPath = path.Replace(".md", "-moved.md", StringComparison.Ordinal), version }, Ct)),
        ("delete", p => p.PostAsJsonAsync($"/private-notes/sync/notes/{noteId}/delete",
            new { baseVersion = version }, Ct)),
    ];

    private static async Task ShouldBePausedAsync(HttpResponseMessage resp, string because)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Locked, because);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, because);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("error").GetString().Should().Be(SyncFailureReasons.Paused, because);
    }

    // ── 決定 1・2: 停止中は 5 つの口をすべて拒み、別の端末も拒む。再開で同じトークンのまま通る ──

    [Fact]
    public async Task 停止中は5つの口がすべて423とsync_pausedで拒み別の端末も拒む()
    {
        var user = NewUser("pause");
        var session = SessionAs(user);
        var laptop = await DeviceAsync(session, "laptop");
        var phone = await DeviceAsync(session, "phone");
        var note = await PushNewAsync(laptop, "notes/a.md");

        // 陽性対照: 停止の前は通る（manifest と pull）。
        (await laptop.GetAsync("/private-notes/sync/manifest", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await phone.GetAsync($"/private-notes/sync/notes/{note.NoteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var plugin in new[] { laptop, phone })
            foreach (var (name, send) in FiveRoutes(note.NoteId, note.Version, "notes/b.md"))
                await ShouldBePausedAsync(await send(plugin), $"停止中の {name}");

        // 拒んだ要求は資料を変えていない（push は新しい資料を作らず、move・delete は効いていない）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var notes = await db.PrivateNotes.AsNoTracking().Where(n => n.OwnerId == user).ToListAsync(Ct);
        notes.Should().ContainSingle();
        notes[0].VaultPath.Should().Be("notes/a.md");
        notes[0].DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task 再開すると同じトークンのまま5つの口が通る()
    {
        var session = SessionAs(NewUser("resume"));
        var plugin = await DeviceAsync(session, "laptop");
        var note = await PushNewAsync(plugin, "notes/a.md");

        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBePausedAsync(await plugin.GetAsync("/private-notes/sync/manifest", Ct), "停止中（前提）");
        (await ResumeAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);

        var routes = FiveRoutes(note.NoteId, note.Version, "notes/b.md");
        (await routes[0].Send(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "manifest");
        (await routes[1].Send(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "pull");
        (await routes[2].Send(plugin)).StatusCode.Should().Be(HttpStatusCode.Created, "push（新規）");
        (await routes[3].Send(plugin)).StatusCode.Should().Be(HttpStatusCode.OK, "move");
        var moved = await plugin.GetFromJsonAsync<SyncPauseTestsManifestEntry[]>("/private-notes/sync/manifest", Ct);
        var version = moved!.Single(e => e.NoteId == note.NoteId).Version;
        (await FiveRoutes(note.NoteId, version, "notes/b.md")[4].Send(plugin)).StatusCode
            .Should().Be(HttpStatusCode.OK, "delete");
    }

    // ── 決定 2: トークン・アカウントの拒否（401）は停止中でも 401 のまま（取り違えない）──

    [Fact]
    public async Task 停止中でも無効なトークンとアカウントの無効化は401のまま()
    {
        var user = NewUser("p401");
        var session = SessionAs(user);
        var plugin = await DeviceAsync(session, "laptop");
        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PluginWith("not-a-token").GetAsync("/private-notes/sync/manifest", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "不正なトークン");
        (await factory.CreateClient().GetAsync("/private-notes/sync/manifest", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "トークンなし");

        factory.OwnerAccounts.Declare(user, OwnerAccountState.Disabled);
        try
        {
            (await plugin.GetAsync("/private-notes/sync/manifest", Ct)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "アカウントの無効化は停止中でも 401（ADR-0114）");
        }
        finally
        {
            factory.OwnerAccounts.Declare(user, OwnerAccountState.Enabled);
        }
        // 陽性対照: 有効に戻れば、停止中の拒否（423）に戻る。
        await ShouldBePausedAsync(await plugin.GetAsync("/private-notes/sync/manifest", Ct), "有効な端末の停止中");
    }

    // ── 決定 3: 同じ停止期間の拒否は 1 行。再開の後の再停止は別の行 ──

    [Fact]
    public async Task 同じ停止期間の拒否は同期履歴の1行にまとまり再停止は別の行になる()
    {
        var user = NewUser("phist");
        var session = SessionAs(user);
        var laptop = await DeviceAsync(session, "laptop");
        var phone = await DeviceAsync(session, "phone");
        var note = await PushNewAsync(laptop, "notes/a.md");
        var before = await HistoryAsync(session);

        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBePausedAsync(await laptop.GetAsync("/private-notes/sync/manifest", Ct), "1 回目");
        await ShouldBePausedAsync(await phone.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("notes/x.md", "x"), Ct), "2 回目（別の端末・別の口）");
        await ShouldBePausedAsync(await laptop.GetAsync($"/private-notes/sync/notes/{note.NoteId}", Ct), "3 回目");

        var during = await HistoryAsync(session);
        var paused = during.Where(e => e.FailureReason == SyncFailureReasons.Paused).ToList();
        paused.Should().ContainSingle("同じ停止期間の拒否は 1 行");
        var row = paused[0];
        row.Outcome.Should().Be(SyncOutcomes.Failure);
        row.RejectedCount.Should().Be(3);
        row.FirstRejectedAt.Should().NotBeNull();
        row.LastRejectedAt.Should().BeOnOrAfter(row.FirstRejectedAt!.Value);
        row.Direction.Should().BeEmpty("方向は空（ADR-0145 決定 3）");
        (row.Added + row.Updated + row.Deleted + row.Conflicted).Should().Be(0, "内訳は空");
        during.Should().HaveCount(before.Count + 1, "停止期間の拒否は 3 回でも 1 行しか増えない");

        // 再開 → 再停止は新しい停止期間（別の行）。
        (await ResumeAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBePausedAsync(await laptop.GetAsync("/private-notes/sync/manifest", Ct), "再停止");
        var after = (await HistoryAsync(session)).Where(e => e.FailureReason == SyncFailureReasons.Paused).ToList();
        after.Should().HaveCount(2);
        after.Single(e => e.Id != row.Id).RejectedCount.Should().Be(1);
        after.Single(e => e.Id == row.Id).RejectedCount.Should().Be(3, "前の停止期間の行は増えない");

        // 構造化ログにも拒否ごとに残る（denied・理由コード）。
        factory.Audit.Entries.Count(e => e.Subject == user && e.Action == "private-note.sync.paused"
            && e.Outcome == "denied" && e.Detail!.Contains($"reason={SyncFailureReasons.Paused}"))
            .Should().Be(4);
    }

    // ── 決定 4: 停止中の SC-19 の変更は再開後の通常の同期で追いつき、競合にしない ──

    [Fact]
    public async Task 停止中にSC19で削除した資料は再開後のmanifestで削除として出て競合は作られない()
    {
        var user = NewUser("pcatch");
        var session = SessionAs(user);
        var plugin = await DeviceAsync(session, "laptop");
        var note = await PushNewAsync(plugin, "notes/a.md");

        (await PauseAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.DeleteAsync($"/private-notes/{note.NoteId}", Ct)).IsSuccessStatusCode
            .Should().BeTrue("SC-19 の操作は停止中も使える（同期トークンの口ではない）");
        (await ResumeAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK);

        var manifest = await plugin.GetFromJsonAsync<SyncPauseTestsManifestEntry[]>("/private-notes/sync/manifest", Ct);
        manifest!.Single(e => e.NoteId == note.NoteId).Deleted.Should().BeTrue();
        (await session.GetFromJsonAsync<List<SyncConflictSummaryDto>>("/private-notes/conflicts/", Ct))
            .Should().BeEmpty("一時停止そのものを競合の原因にしない");
    }

    // ── 決定 1: 冪等・状態の応答・有効期限を変えない ──

    [Fact]
    public async Task 一時停止と再開は冪等で状態を返しトークンの有効期限を変えない()
    {
        var user = NewUser("pidem");
        var session = SessionAs(user);
        await DeviceAsync(session, "laptop");
        var expiresBefore = (await session.GetFromJsonAsync<List<SyncDeviceDto>>("/private-notes/devices/", Ct))!
            .Single().ExpiresAt;

        var initial = await session.GetFromJsonAsync<SyncSettingsDto>("/private-notes/sync-settings/", Ct);
        initial!.SyncPaused.Should().BeFalse("既定は有効");
        initial.SyncPausedAt.Should().BeNull();

        var first = await (await PauseAsync(session)).Content.ReadFromJsonAsync<SyncSettingsDto>(Ct);
        var second = await (await PauseAsync(session)).Content.ReadFromJsonAsync<SyncSettingsDto>(Ct);
        first!.SyncPaused.Should().BeTrue();
        second!.SyncPausedAt.Should().Be(first.SyncPausedAt, "停止中の一時停止は停止期間を変えない");

        var resumed = await (await ResumeAsync(session)).Content.ReadFromJsonAsync<SyncSettingsDto>(Ct);
        resumed!.SyncPaused.Should().BeFalse();
        resumed.SyncPausedAt.Should().BeNull();
        (await ResumeAsync(session)).StatusCode.Should().Be(HttpStatusCode.OK, "有効なときの再開も冪等");

        (await session.GetFromJsonAsync<List<SyncDeviceDto>>("/private-notes/devices/", Ct))!
            .Single().ExpiresAt.Should().Be(expiresBefore, "一時停止は有効期限を止めない・変えない（決定 1）");
    }

    [Fact]
    public async Task 一時停止と再開は主体の無い要求を401で拒む()
    {
        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        (await anonymous.PostAsync("/private-notes/sync-settings/pause", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/private-notes/sync-settings/resume", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<List<SyncHistoryEntryDto>> HistoryAsync(HttpClient session)
        => (await session.GetFromJsonAsync<List<SyncHistoryEntryDto>>("/private-notes/sync-history", Ct))!;
}

// manifest の 1 行のうち、この試験が読む項目だけ。
internal sealed record SyncPauseTestsManifestEntry(Guid NoteId, int Version, bool Deleted);
