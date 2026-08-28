using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using DocumentService.Features.ObsidianSync;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests;

// FR-20, UC-11, ADR-0037 決定 2〜5・7〜9・14, ADR-0046 D-03, [[IADR-0270]] 決定 3・5・7:
// Obsidian プラグイン向け同期プロトコル（/private-notes/sync）。
//
// 🔴 スコープの否定形（他者の資料・組織文書・共有された資料が見えない）は、
// 陽性対照（自分の資料は見える）と対で置く —— 「常に 404 を返す実装」でも否定形だけは緑になる。
public class ObsidianSyncProtocolTests(TestWebApplicationFactory factory)
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
            new { deviceName });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>())!.Token;
    }

    private static object PushBody(string title, string path, params string[] edits) => new
    {
        vaultPath = path,
        title,
        edits = edits.Select(c => new { content = c }).ToList(),
    };

    // FR-20（陽性対照）: 有効なトークンで自分の資料を作成・一覧・取得できる。
    [Fact]
    public async Task 有効なトークンで自分の資料を作成しマニフェストと本文取得ができる()
    {
        var token = await IssueTokenAsync($"alice-{Guid.NewGuid():N}"[..20]);
        var plugin = PluginWith(token);

        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("メモ", "notes/memo.md", "# こんにちは"), TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        var manifest = await plugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        manifest.Should().ContainSingle(e => e.NoteId == created!.NoteId);
        manifest![0].VaultPath.Should().Be("notes/memo.md");
        manifest[0].Deleted.Should().BeFalse();

        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{created!.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("# こんにちは", "本文は切り詰めず往復する");
    }

    // FR-20, ADR-0037 課題 2（否定形）: トークン無し・出鱈目・失効・期限切れはいずれも同じ 401。
    [Fact]
    public async Task 無効なトークンはすべて401になる()
    {
        var user = $"bob-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);

        // 無し
        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/private-notes/sync/manifest", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // 出鱈目
        (await PluginWith("deadbeef").GetAsync("/private-notes/sync/manifest", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // 失効（個別失効の後は使えない）
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "phone" }, TestContext.Current.CancellationToken);
        var device = await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(TestContext.Current.CancellationToken);
        (await session.DeleteAsync($"/private-notes/devices/{device!.DeviceId}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await PluginWith(device.Token).GetAsync("/private-notes/sync/manifest", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // FR-20 受け入れ基準（否定形＋陽性対照）: 同期用の資格情報で、他の利用者が所有する資料
    // （**自身に共有されたものを含む**）および組織文書を取得できない。
    [Fact]
    public async Task 他者の資料は共有されていても組織文書でも同期資格情報から到達できない()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var alice = $"alice-{suffix}";
        var bob = $"bob-{suffix}";

        // bob の個人資料（bob 自身の同期経路で作成 = 陽性対照の下ごしらえ）
        var bobToken = await IssueTokenAsync(bob, "bob-pc");
        var bobPush = await PluginWith(bobToken).PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("bobの秘密", "secret.md", "bob only"), TestContext.Current.CancellationToken);
        var bobNote = await bobPush.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        // bob が alice へ共有する（閲覧の共有であって同期の対象化ではない）
        var share = await SessionAs(bob).PostAsJsonAsync(
            $"/documents/{bobNote!.NoteId}/shares", new { subjectType = "user", subjectId = alice }, TestContext.Current.CancellationToken);
        share.StatusCode.Should().Be(HttpStatusCode.Created);

        // 組織文書（管理者経路・doc_scope 無し）
        var orgResp = await SessionAs("admin").PostAsJsonAsync("/documents", new
        {
            title = "組織文書",
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        var orgDoc = await orgResp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        var aliceToken = await IssueTokenAsync(alice, "alice-pc");
        var alicePlugin = PluginWith(aliceToken);

        // 否定形: マニフェストに他者の資料・組織文書が現れない
        var manifest = await alicePlugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        manifest.Should().BeEmpty("alice の同期スコープには何も無い");

        // 否定形: ID を直に指しても 404（存在秘匿。共有済みでも同期経路からは読めない）
        (await alicePlugin.GetAsync($"/private-notes/sync/notes/{bobNote.NoteId}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await alicePlugin.GetAsync($"/private-notes/sync/notes/{orgDoc!.Id}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await alicePlugin.PostAsJsonAsync($"/private-notes/sync/notes/{bobNote.NoteId}/delete",
            new { }, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 陽性対照: bob 自身は自分の資料へ到達できる（「常に 404」の実装でないことの証拠）
        (await PluginWith(bobToken).GetAsync($"/private-notes/sync/notes/{bobNote.NoteId}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-19, ADR-0037 決定 8: オフラインで 10 回編集して 1 回同期した場合も 10 版として保持する。
    [Fact]
    public async Task 一回の同期に10編集を載せると10版が刻まれる()
    {
        var token = await IssueTokenAsync($"carol-{Guid.NewGuid():N}"[..20]);
        var plugin = PluginWith(token);

        var edits = Enumerable.Range(1, 10).Select(i => $"版 {i}").ToArray();
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("十版", "ten.md", edits), TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);
        created!.Version.Should().Be(10, "1 編集 = 1 版（同期 1 回へ丸めない）");

        var versions = await factory.CreateClient().GetFromJsonAsync<List<DocumentVersionDto>>(
            $"/documents/{created.NoteId}/versions", TestContext.Current.CancellationToken);
        versions.Should().HaveCount(10);

        // 最終状態は最後の編集の本文である
        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{created.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("版 10");
    }

    // ADR-0037 決定 7: 競合はサーバで自動解決せず、409 でクライアントへ返す。
    [Fact]
    public async Task 版がずれたpushは409になり後勝ちで上書きされない()
    {
        var token = await IssueTokenAsync($"dave-{Guid.NewGuid():N}"[..20]);
        var plugin = PluginWith(token);

        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("競合", "conflict.md", "初版"), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        // 端末 A が v1 → v2 へ更新
        var updateA = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note!.NoteId,
            vaultPath = "conflict.md",
            title = "競合",
            baseVersion = note.Version,
            edits = new[] { new { content = "端末Aの編集" } },
        }, TestContext.Current.CancellationToken);
        updateA.StatusCode.Should().Be(HttpStatusCode.OK);

        // 端末 B が古い版（v1）を土台に push → 自動解決せず 409
        var updateB = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note.NoteId,
            vaultPath = "conflict.md",
            title = "競合",
            baseVersion = note.Version,
            edits = new[] { new { content = "端末Bの編集" } },
        }, TestContext.Current.CancellationToken);
        updateB.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 陽性対照: サーバ本文は端末 A のまま（後勝ちで上書きされていない）
        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("端末Aの編集");
    }

    // FR-20, ADR-0037 決定 5: Obsidian 側の削除はサーバ側で論理削除に留まる（90 日保管・復元可）。
    [Fact]
    public async Task 同期経由の削除は論理削除でありサーバから即時消滅しない()
    {
        var user = $"erin-{Guid.NewGuid():N}"[..20];
        var token = await IssueTokenAsync(user);
        var plugin = PluginWith(token);

        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("消すメモ", "del.md", "内容"), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        var del = await plugin.PostAsJsonAsync(
            $"/private-notes/sync/notes/{note!.NoteId}/delete", new { }, TestContext.Current.CancellationToken);
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        // マニフェストには deleted=true で残る（KB が正。プラグインが削除を検知できる）
        var manifest = await plugin.GetFromJsonAsync<List<SyncManifestEntry>>(
            "/private-notes/sync/manifest", TestContext.Current.CancellationToken);
        manifest.Should().ContainSingle(e => e.NoteId == note.NoteId && e.Deleted);

        // SC-19 から復元できる（物理削除ではない）
        var restore = await SessionAs(user).PostAsync(
            $"/private-notes/{note.NoteId}/restore", null, TestContext.Current.CancellationToken);
        restore.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-19 受け入れ基準 / FR-21 受け入れ基準 ⑩, ADR-0037 フォローアップ 8, ADR-0054:
    // 同期経由の新規作成の既定値。
    // doc_scope=private-note・owner=本人・機密区分 restricted・3 トグル OFF・共有 0 件。
    //
    // 🔴 **作成経路は 2 本ある**（SC-19 の `POST /private-notes/` と本経路）。⑩ は登録経路の基準で
    // あるから、**両方を測る** —— 既定値は 1 か所（`PrivateNoteDefaults`）で持っているが、
    // 片方だけ測ると将来どちらかが分岐したときに静かに割れる。
    [Fact]
    public async Task 同期経由の新規作成はフェイルセーフ既定で作られる()
    {
        var user = $"frank-{Guid.NewGuid():N}"[..20];
        var token = await IssueTokenAsync(user);

        var push = await PluginWith(token).PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("既定値", "default.md", "本文"), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        var doc = await factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/documents/{note!.NoteId}", TestContext.Current.CancellationToken);
        doc!.Attributes.Should().Contain("doc_scope", "private-note");
        doc.Attributes.Should().Contain("owner", user);
        doc.Attributes.Should().Contain("confidentiality", "restricted",
            "プラグイン流入は画面バリデーションを経由しないため、サーバ側の既定で最も厳しい区分を適用する");
        // FR-21 ⑨ / [[IADR-0283]] 決定 4: AI 入力の既定 OFF は**属性としても明示**される。
        doc.Attributes.Should().Contain(AiInputExposure.AttributeKey, AiInputExposure.Excluded);
        AiInputExposure.IsAllowed(doc.Attributes).Should().BeFalse();

        var list = await SessionAs(user).GetFromJsonAsync<PrivateNoteListResponse>(
            "/private-notes/", TestContext.Current.CancellationToken);
        var dto = list!.Notes.Single(n => n.Id == note.NoteId);
        dto.IncludeInSearch.Should().BeFalse("既定は OFF");
        dto.IncludeInGraph.Should().BeFalse("既定は OFF");
        dto.IncludeInAi.Should().BeFalse("既定は OFF");

        var shares = await SessionAs(user).GetFromJsonAsync<List<DocumentShareDto>>(
            $"/documents/{note.NoteId}/shares", TestContext.Current.CancellationToken);
        shares.Should().BeEmpty("公開範囲の既定は非公開（所有者のみ）");
    }

    // FR-21 受け入れ基準 ⑥ と同一の上限: 1 MB 超の本文は 413 で拒否し、切り詰めない。
    [Fact]
    public async Task 一メガバイト超の本文は413で拒否される()
    {
        var token = await IssueTokenAsync($"grace-{Guid.NewGuid():N}"[..20]);
        var plugin = PluginWith(token);

        var tooLarge = new string('あ', 350_000); // UTF-8 で約 1.05 MB
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("大きい", "big.md", tooLarge), TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // 陽性対照: 1 MB 以下は通る
        var ok = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("普通", "ok.md", new string('a', 1000)), TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // FR-20 受け入れ基準, ADR-0037 決定 9: 同期の実行記録は「誰が・いつ・何件」。
    // タイトル・内容を記録しない。
    [Fact]
    public async Task 同期の監査ログは件数のみでタイトルを含まない()
    {
        var user = $"heidi-{Guid.NewGuid():N}"[..20];
        var token = await IssueTokenAsync(user);
        var plugin = PluginWith(token);

        const string secretTitle = "監査に漏れてはならない題名";
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody(secretTitle, "audit.md", "秘密の本文"), TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);
        (await plugin.GetAsync($"/private-notes/sync/notes/{note!.NoteId}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var pushEntries = factory.Audit.OfAction("private-note.sync.push")
            .Where(e => e.Subject == user).ToList();
        pushEntries.Should().ContainSingle("push 1 回につき実行記録 1 件");
        pushEntries[0].Detail.Should().Contain("count=1");

        var pullEntries = factory.Audit.OfAction("private-note.sync.pull")
            .Where(e => e.Subject == user).ToList();
        pullEntries.Should().ContainSingle();

        foreach (var entry in factory.Audit.Entries)
        {
            (entry.Detail ?? string.Empty).Should().NotContain(secretTitle,
                "監査ログに資料のタイトルを書かない（ADR-0037 決定 9）");
            (entry.Detail ?? string.Empty).Should().NotContain("秘密の本文");
        }
    }
}
