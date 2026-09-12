using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.SyncConflicts;

// FR-20, UC-11, SC-20 主要素 5, ADR-0037 決定 7・9・17, [[IADR-0352]], #1442:
// 同期競合の記録（push の 409）・一覧・詳細・解決の 3 択。
//
// 🔴 **プロトコルの 409 応答は変えていない。** 競合を記録するようになっても、プラグインから見た
// 状態・本文は同じである（`ObsidianSyncProtocolTests` が別途固定しているが、ここでも対で測る）。
[Trait("TestKind", "Integration")]
public class SyncConflictEndpointTests(TestWebApplicationFactory factory)
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

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync(
        string prefix, string deviceName = "pc")
    {
        var user = NewUser(prefix);
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName },
            TestContext.Current.CancellationToken);
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
        return (user, session, PluginWith(token));
    }

    private async Task<PushNoteResponse> PushNewAsync(HttpClient plugin, string title, string path,
        string content)
    {
        var resp = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = path,
            title,
            edits = new[] { new { content } },
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> PushUpdateAsync(HttpClient plugin, Guid noteId, string title,
        string path, int baseVersion, string content)
        => plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId,
            vaultPath = path,
            title,
            baseVersion,
            edits = new[] { new { content } },
        }, TestContext.Current.CancellationToken);

    private async Task<List<SyncConflictSummaryDto>> ConflictsAsync(HttpClient session)
        => (await session.GetFromJsonAsync<List<SyncConflictSummaryDto>>(
            "/private-notes/conflicts", TestContext.Current.CancellationToken))!;

    // 競合を 1 件だけ起こす（端末 A が版を進め、同じ端末が古い版を土台に push する）。
    private async Task<(PushNoteResponse Note, HttpClient Session, string User, HttpClient Plugin)>
        ConflictedNoteAsync(string prefix, string path = "conf/memo.md")
    {
        var (user, session, plugin) = await OwnerAsync(prefix);
        var note = await PushNewAsync(plugin, "競合する資料", path, "初版");

        (await PushUpdateAsync(plugin, note.NoteId, "競合する資料", path, note.Version, "サーバ版"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PushUpdateAsync(plugin, note.NoteId, "競合する資料", path, note.Version, "ローカル版"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        return (note, session, user, plugin);
    }

    // ── 記録（push の 409）─────────────────────────────────────
    //
    // 🔴 **409 の応答は変わっていない**（状態・本文の鍵）。記録は応答の外側の出来事である。
    [Fact]
    public async Task 版がずれたpushは409のまま競合を記録する()
    {
        var (note, session, _, _) = await ConflictedNoteAsync("c-rec");

        var conflicts = await ConflictsAsync(session);
        conflicts.Should().ContainSingle();
        conflicts[0].NoteId.Should().Be(note.NoteId);
        conflicts[0].LocalBaseVersion.Should().Be(note.Version, "端末が土台にしていた版");
        conflicts[0].ServerVersion.Should().Be(note.Version + 1, "検出時のサーバの版");
        conflicts[0].DeviceName.Should().Be("pc");
        conflicts[0].VaultPath.Should().Be("conf/memo.md");
    }

    // ADR-0037 決定 7: **同じ資料・同じ端末の未解決競合は上書きする**（行を増やさない）。
    // 端末がオフラインのまま何度も試すと、同じ競合で一覧が埋まって解決すべきものが見えなくなる。
    [Fact]
    public async Task 同じ資料と端末の再試行は競合の行を増やさず上書きする()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-dup");

        (await PushUpdateAsync(plugin, note.NoteId, "競合する資料", "conf/memo.md", note.Version,
            "もう一度のローカル版")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var conflicts = await ConflictsAsync(session);
        conflicts.Should().ContainSingle("同じ資料・同じ端末の未解決競合は 1 行である");

        // 上書きされたのは**本文**である（最新のローカル本文が差分に出る）。
        var detail = await session.GetFromJsonAsync<SyncConflictDetailDto>(
            $"/private-notes/conflicts/{conflicts[0].Id}", TestContext.Current.CancellationToken);
        detail!.LocalContent.Should().Be("もう一度のローカル版");
    }

    // ADR-0037 決定 7: 正しい `baseVersion` で push が通れば、プラグイン側で解決済みである。
    // **一覧から消える**（`client` として閉じる）。
    [Fact]
    public async Task 正しい版でのpush成功は未解決の競合を閉じる()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-close");
        (await ConflictsAsync(session)).Should().ContainSingle("陽性対照: 閉じる前は 1 件ある");

        (await PushUpdateAsync(plugin, note.NoteId, "競合する資料", "conf/memo.md",
            note.Version + 1, "端末側で解決した本文")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ConflictsAsync(session)).Should().BeEmpty("プラグイン側で解決されたので閉じる");
    }

    // ── 一覧・詳細 ───────────────────────────────────────────
    [Fact]
    public async Task 詳細はローカル版とサーバ版の両方の本文を返す()
    {
        var (_, session, _, _) = await ConflictedNoteAsync("c-detail");
        var conflicts = await ConflictsAsync(session);

        var detail = await session.GetFromJsonAsync<SyncConflictDetailDto>(
            $"/private-notes/conflicts/{conflicts[0].Id}", TestContext.Current.CancellationToken);

        detail!.LocalContent.Should().Be("ローカル版");
        detail.ServerContent.Should().Be("サーバ版");
        detail.Id.Should().Be(conflicts[0].Id);
    }

    // 🔴 **他人の競合は 404**（403 にすると他人の競合 ID の実在が漏れる）。
    // 陽性対照は同じ試験の中の「本人は見える」である。
    [Fact]
    public async Task 他人の競合は404で本人は見える()
    {
        var (_, session, _, _) = await ConflictedNoteAsync("c-own");
        var conflicts = await ConflictsAsync(session);
        var stranger = SessionAs(NewUser("c-stranger"));

        (await stranger.GetAsync($"/private-notes/conflicts/{conflicts[0].Id}",
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ConflictsAsync(stranger)).Should().BeEmpty("他人の競合は一覧にも現れない");

        (await session.GetAsync($"/private-notes/conflicts/{conflicts[0].Id}",
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 解決の 3 択 ──────────────────────────────────────────
    //
    // `local`: ローカル本文が**新しい版**として載る（版が 1 つ進む）。
    [Fact]
    public async Task localの解決はローカル本文を新しい版として書く()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-local");
        var conflicts = await ConflictsAsync(session);

        var resp = await session.PostAsJsonAsync(
            $"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Local),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = (await resp.Content.ReadFromJsonAsync<ResolveSyncConflictResponse>(
            TestContext.Current.CancellationToken))!;
        body.Resolution.Should().Be(SyncConflictResolutions.Local);
        body.NoteVersion.Should().Be(note.Version + 2, "サーバ版（+1）を土台に 1 版進む");
        body.CreatedNoteId.Should().BeNull("別名資料を作るのは both だけである");

        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("ローカル版");
        (await ConflictsAsync(session)).Should().BeEmpty("解決した競合は一覧から消える");
    }

    // `server`: **資料は 1 バイトも変わらない**（版も進まない）。
    [Fact]
    public async Task serverの解決は資料を変えない()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-server");
        var conflicts = await ConflictsAsync(session);

        var resp = await session.PostAsJsonAsync(
            $"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Server),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = (await resp.Content.ReadFromJsonAsync<ResolveSyncConflictResponse>(
            TestContext.Current.CancellationToken))!;
        body.NoteVersion.Should().Be(note.Version + 1, "版は進まない");
        body.CreatedNoteId.Should().BeNull();

        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("サーバ版", "ローカル本文は採らない");
    }

    // `both`: サーバ版はそのまま、ローカル本文が**別名の新規資料**になる。
    [Fact]
    public async Task bothの解決は別名の新規資料を作り元の資料を変えない()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-both");
        var conflicts = await ConflictsAsync(session);

        var resp = await session.PostAsJsonAsync(
            $"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Both),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = (await resp.Content.ReadFromJsonAsync<ResolveSyncConflictResponse>(
            TestContext.Current.CancellationToken))!;
        body.CreatedNoteId.Should().NotBeNull();
        body.NoteVersion.Should().Be(note.Version + 1, "元の資料の版は進まない");

        // 元の資料はサーバ版のまま。
        var original = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        original!.Content.Should().Be("サーバ版");

        // 別名の新規資料にローカル本文が入っている。
        var created = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{body.CreatedNoteId}", TestContext.Current.CancellationToken);
        created!.Content.Should().Be("ローカル版");
        created.VaultPath.Should().StartWith("conf/memo (競合 ").And.EndWith(").md",
            "拡張子の前へ入れる（末尾へ付けると Vault で開けないファイルになる）");
        created.Title.Should().StartWith("競合する資料 (競合 ");
    }

    // 🔴 **解決済みへの再解決は 409**（黙って 2 度目を適用すると版がもう 1 つ進む）。
    // 詳細（GET）のほうは **404** である（画面が「まだ選べる」と見せないため）。
    [Fact]
    public async Task 解決済みの競合は再解決が409で詳細が404になる()
    {
        var (_, session, _, _) = await ConflictedNoteAsync("c-again");
        var conflicts = await ConflictsAsync(session);
        var path = $"/private-notes/conflicts/{conflicts[0].Id}";

        (await session.PostAsJsonAsync($"{path}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Server),
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await session.PostAsJsonAsync($"{path}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Local),
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await session.GetAsync(path, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 **自動解決の値は無い。** `client` は後段が競合を閉じるときの記録用であり、
    // 端点から指定できると「自動解決した」記録を外から作れてしまう。
    [Theory]
    [InlineData("")]
    [InlineData("latest-wins")]
    [InlineData("client")]
    public async Task 選べない解決方法は400になる(string resolution)
    {
        var (_, session, _, _) = await ConflictedNoteAsync("c-400");
        var conflicts = await ConflictsAsync(session);

        var resp = await session.PostAsJsonAsync(
            $"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(resolution), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("resolution");
    }

    // 陽性対照（対）: 3 択はいずれも受け付けられる（「常に 400」の実装を排除する）。
    [Theory]
    [InlineData(SyncConflictResolutions.Local)]
    [InlineData(SyncConflictResolutions.Server)]
    [InlineData(SyncConflictResolutions.Both)]
    public async Task 三択はいずれも受け付けられる(string resolution)
    {
        var (_, session, _, _) = await ConflictedNoteAsync($"c-ok{resolution[..2]}");
        var conflicts = await ConflictsAsync(session);

        (await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(resolution), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ADR-0037 決定 9: 監査は「誰が・いつ・何件」。**タイトル・本文・Vault パスを書かない。**
    [Fact]
    public async Task 競合の監査ログはタイトルも本文も書かない()
    {
        var (_, _, user, _) = await ConflictedNoteAsync("c-audit", "conf/秘密のタイトル.md");

        var records = factory.Audit.OfAction("private-note.sync.conflict")
            .Where(r => r.Subject == user).ToList();

        records.Should().ContainSingle();
        records[0].Detail.Should().Contain("count=1").And.NotContain("秘密のタイトル");
        records[0].Detail.Should().NotContain("競合する資料");
    }

    // 解決したらローカル本文は残さない（保持期限の裁定を待たずに消える分は消す）。
    [Fact]
    public async Task 解決するとローカル本文は消える()
    {
        var (_, session, _, _) = await ConflictedNoteAsync("c-wipe");
        var conflicts = await ConflictsAsync(session);

        string uri;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            uri = (await db.SyncConflicts.FirstAsync(c => c.Id == conflicts[0].Id,
                TestContext.Current.CancellationToken)).LocalContentUri;
        }
        factory.Storage.Texts.ContainsKey(uri).Should().BeTrue("陽性対照: 記録時には保存されている");

        (await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Server),
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Storage.Texts.ContainsKey(uri).Should().BeFalse();
    }
}
