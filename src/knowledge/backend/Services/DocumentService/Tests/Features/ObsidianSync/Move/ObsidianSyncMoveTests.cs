using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using DocumentService.Features.ObsidianSync.Manifest;
using DocumentService.Features.ObsidianSync.Move;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.ObsidianSync.Move;

// FR-20, UC-11, ADR-0037 決定 2・7・9・14, [[IADR-0360]]:
// リネーム（`POST /private-notes/sync/notes/{id}/move`）。Obsidian 側の名前変更の伝播。
//
// 🔴 否定形（他人の資料へ届かない・版がずれたら動かない・重複する名前へ移せない）は、
// **陽性対照と対で**置く —— 「常に 404／常に 409 を返す実装」でも否定形だけは緑になる。
[Trait("TestKind", "Integration")]
public class ObsidianSyncMoveTests(TestWebApplicationFactory factory)
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

    private async Task<string> IssueTokenAsync(string user, string deviceName = "laptop")
    {
        var resp = await SessionAs(user).PostAsJsonAsync("/private-notes/devices",
            new { deviceName }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
    }

    private static string UserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static object PushBody(string title, string path, params string[] edits) => new
    {
        vaultPath = path,
        title,
        edits = edits.Select(c => new { content = c }).ToList(),
    };

    private static async Task<PushNoteResponse> CreateAsync(HttpClient plugin, string title,
        string path, string content = "本文")
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody(title, path, content), TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient plugin, Guid noteId,
        string vaultPath, int? version)
        => plugin.PostAsJsonAsync($"/private-notes/sync/notes/{noteId}/move",
            new { vaultPath, version }, TestContext.Current.CancellationToken);

    // A1（陽性対照）: リネームが manifest に反映され、**版履歴は保たれる**（版が増えも減りもしない）。
    [Fact]
    public async Task リネームはマニフェストに反映され版履歴を進めない()
    {
        var token = await IssueTokenAsync(UserName("mallory"));
        var plugin = PluginWith(token);
        var note = await CreateAsync(plugin, "旧名", "notes/old.md", "中身");

        var move = await MoveAsync(plugin, note.NoteId, "notes/new.md", note.Version);
        move.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await move.Content.ReadFromJsonAsync<MoveNoteResponse>(
            TestContext.Current.CancellationToken);
        moved!.VaultPath.Should().Be("notes/new.md");
        moved.Version.Should().Be(note.Version, "リネームは版を進めない（本文が変わっていない）");

        var manifest = await plugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        manifest.Should().ContainSingle(e => e.NoteId == note.NoteId);
        manifest![0].VaultPath.Should().Be("notes/new.md");
        manifest[0].Version.Should().Be(note.Version);

        // 本文は動いていない（名前だけの操作である）
        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("中身");
        pull.VaultPath.Should().Be("notes/new.md");

        var versions = await factory.CreateClient().GetFromJsonAsync<List<DocumentVersionDto>>(
            $"/documents/{note.NoteId}/versions", TestContext.Current.CancellationToken);
        versions.Should().ContainSingle("リネームは版履歴に行を足さない");

        // 冪等: 同じ名前への move は 200 のまま（自分自身と衝突させない）
        var again = await MoveAsync(plugin, note.NoteId, "notes/new.md", note.Version);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // A2（否定形＋陽性対照）: 既存の有効な資料と同じ名前へは移せず、**どちらの資料も動かない**。
    [Fact]
    public async Task 既存の有効な資料と重なる名前へのリネームは409で上書きしない()
    {
        var token = await IssueTokenAsync(UserName("nancy"));
        var plugin = PluginWith(token);
        var a = await CreateAsync(plugin, "A", "notes/a.md", "Aの中身");
        var b = await CreateAsync(plugin, "B", "notes/b.md", "Bの中身");

        var move = await MoveAsync(plugin, a.NoteId, "notes/b.md", a.Version);
        move.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await move.Content.ReadFromJsonAsync<Dictionary<string, string>>(
            TestContext.Current.CancellationToken);
        body!["error"].Should().Be("vault_path_conflict");
        body["vaultPath"].Should().Be("notes/b.md");

        var manifest = await plugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        var entries = manifest!;
        entries.Single(e => e.NoteId == a.NoteId).VaultPath.Should().Be("notes/a.md");
        entries.Single(e => e.NoteId == b.NoteId).VaultPath.Should().Be("notes/b.md");

        // 陽性対照: 空いている名前へは同じ操作が通る（「常に 409」の実装ではない）
        (await MoveAsync(plugin, a.NoteId, "notes/c.md", a.Version)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // A3（否定形）: 古い版でのリネームは 409。サーバは自動解決しない（ADR-0037 決定 7）。
    [Fact]
    public async Task 版がずれたリネームは409になり名前は変わらない()
    {
        var token = await IssueTokenAsync(UserName("olivia"));
        var plugin = PluginWith(token);
        var note = await CreateAsync(plugin, "競合", "notes/race.md", "初版");

        // 別端末（＝サーバ側）が版を進める
        var update = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note.NoteId,
            vaultPath = "notes/race.md",
            title = "競合",
            baseVersion = note.Version,
            edits = new[] { new { content = "二版" } },
        }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await MoveAsync(plugin, note.NoteId, "notes/renamed.md", note.Version);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await stale.Content.ReadFromJsonAsync<Dictionary<string, object>>(
            TestContext.Current.CancellationToken);
        conflict!["error"].ToString().Should().Be("version_conflict");

        var manifest = await plugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        var current = manifest!.Single();
        current.VaultPath.Should().Be("notes/race.md", "版がずれた要求で名前は動かない");

        // 陽性対照: 現在版を土台にすれば通る
        (await MoveAsync(plugin, note.NoteId, "notes/renamed.md", current.Version)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // version を省いたら 400（楽観ロックを素通りさせない）
        var noVersion = await plugin.PostAsJsonAsync(
            $"/private-notes/sync/notes/{note.NoteId}/move", new { vaultPath = "notes/x.md" },
            TestContext.Current.CancellationToken);
        noVersion.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // A4（否定形＋陽性対照）: 他人の資料・存在しない ID は 404（存在秘匿。403 にしない）。
    [Fact]
    public async Task 他人の資料のリネームは404で存在ごと秘匿される()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var alice = $"alice-{suffix}";
        var bob = $"bob-{suffix}";

        var bobToken = await IssueTokenAsync(bob, "bob-pc");
        var bobNote = await CreateAsync(PluginWith(bobToken), "bobの資料", "secret.md", "bob only");

        var alicePlugin = PluginWith(await IssueTokenAsync(alice, "alice-pc"));
        (await MoveAsync(alicePlugin, bobNote.NoteId, "stolen.md", bobNote.Version)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await MoveAsync(alicePlugin, Guid.NewGuid(), "ghost.md", 1)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // 陽性対照: bob 自身の同じ操作は通る（「常に 404」の実装ではない）
        (await MoveAsync(PluginWith(bobToken), bobNote.NoteId, "renamed.md", bobNote.Version))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // bob の資料の名前は alice の要求で動いていない
        var manifest = await PluginWith(bobToken).GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        manifest!.Single().VaultPath.Should().Be("renamed.md");

        // トークンが無ければ 401（理由を問わない）
        (await factory.CreateClient().PostAsJsonAsync(
            $"/private-notes/sync/notes/{bobNote.NoteId}/move",
            new { vaultPath = "x.md", version = 1 }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // A5（否定形）: 論理削除済みの資料は 409 `deleted`（復元してから同期する。push と同形）。
    [Fact]
    public async Task 論理削除済みの資料のリネームは409deletedになる()
    {
        var user = UserName("peggy");
        var plugin = PluginWith(await IssueTokenAsync(user));
        var note = await CreateAsync(plugin, "消えた", "notes/gone.md");

        (await plugin.PostAsJsonAsync($"/private-notes/sync/notes/{note.NoteId}/delete",
            new { }, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var move = await MoveAsync(plugin, note.NoteId, "notes/back.md", note.Version);
        move.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await move.Content.ReadFromJsonAsync<Dictionary<string, object>>(
            TestContext.Current.CancellationToken);
        body!["error"].ToString().Should().Be("deleted");

        // 陽性対照: 復元すれば同じ操作が通る
        (await SessionAs(user).PostAsync($"/private-notes/{note.NoteId}/restore", null,
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MoveAsync(plugin, note.NoteId, "notes/back.md", note.Version)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // A6, ADR-0037 決定 9: 監査は「誰が・いつ・何件」。
    // 🔴 `vaultPath` はファイル名＝実質的な題名であり、記録すればタイトル秘匿の抜け道になる。
    [Fact]
    public async Task リネームの監査ログは件数のみでパスを含まない()
    {
        var user = UserName("quinn");
        var plugin = PluginWith(await IssueTokenAsync(user));
        const string secretPath = "notes/監査に漏れてはならない題名.md";
        var note = await CreateAsync(plugin, "題名", "notes/plain.md");

        (await MoveAsync(plugin, note.NoteId, secretPath, note.Version)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var entries = factory.Audit.OfAction("private-note.sync.move")
            .Where(e => e.Subject == user).ToList();
        entries.Should().ContainSingle("move 1 回につき実行記録 1 件");
        entries[0].Detail.Should().Contain("count=1");

        foreach (var entry in factory.Audit.Entries)
        {
            (entry.Detail ?? string.Empty).Should().NotContain("監査に漏れてはならない題名",
                "監査ログに vaultPath（＝実質的な題名）を書かない");
        }
    }
}
