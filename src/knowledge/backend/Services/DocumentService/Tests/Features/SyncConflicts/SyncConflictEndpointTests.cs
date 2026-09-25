using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
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

    // ── 解決と索引の生産側（発行の門）───────────────────────────
    //
    // FR-19, FR-20, ADR-0061 決定 1・2, [[IADR-0396]] 決定 4, [[IADR-0455]] 決定 1, #1474:
    // 🔴 **本文を書き換える解決は、発行の門を通して `DocumentUpdated` を出す。** 出さないと、露出 ON の
    // 個人資料は本文だけが新しくなり、索引（検索・グラフ）は**古い本文のまま**残る。
    // 陰性（出ない）の主張には、同じ試験の中で陽性対照（解決が効いている／配線が生きている）を対で置く。
    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. Bus.PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == documentId)];

    private static async Task ExposeToSearchAsync(HttpClient session, Guid noteId)
        => (await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure",
            new UpdateExposureRequest(true, false, false), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<ResolveSyncConflictResponse> ResolveOnlyConflictAsync(HttpClient session,
        string resolution)
    {
        var conflicts = await ConflictsAsync(session);
        conflicts.Should().ContainSingle();
        var resp = await session.PostAsJsonAsync(
            $"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(resolution), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ResolveSyncConflictResponse>(
            TestContext.Current.CancellationToken))!;
    }

    // #1474（PR #1476 のフェーズ末監査 D1）: **ゴミ箱の資料へ本文を書く解決は 409 `deleted`**（push・移動と同じ）。
    // 🔴 通すと、利用者がゴミ箱へ移した資料の本文が新しくなり、露出 ON なら索引へ再発行される。
    // 陽性対照: 同じゴミ箱の資料でも `server`（資料を変えない）は通り、競合を片付けられる。
    [Theory]
    [InlineData(SyncConflictResolutions.Local)]
    [InlineData(SyncConflictResolutions.Both)]
    public async Task ゴミ箱の資料へ本文を書く解決は409で発行もしない(string resolution)
    {
        var ct = TestContext.Current.CancellationToken;
        var (note, session, _, plugin) = await ConflictedNoteAsync($"c-trash-{resolution}");
        await ExposeToSearchAsync(session, note.NoteId);
        (await session.DeleteAsync($"/private-notes/{note.NoteId}", ct)).IsSuccessStatusCode
            .Should().BeTrue("前提: 資料をゴミ箱へ移せている");
        var before = UpdatesFor(note.NoteId).Count;
        var conflicts = await ConflictsAsync(session);
        conflicts.Should().ContainSingle("前提: ゴミ箱の資料の競合も一覧に残っている");

        var resp = await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(resolution), ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync(ct)).Should().Contain("deleted");
        UpdatesFor(note.NoteId).Count.Should().Be(before, "ゴミ箱の資料を索引へ再発行しない");
        (await ConflictsAsync(session)).Should().ContainSingle("拒否したので競合は未解決のまま");

        // 陽性対照: `server` はゴミ箱の資料でも通る（資料を変えない解決なので）。
        var server = await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Server), ct);
        server.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = plugin;
    }

    // `local` は属性を変えずに本文だけを書き換える —— 単純な門（今通るとき）で 1 回だけ出す。
    [Fact]
    public async Task localの解決は露出ONの個人資料で新しい本文のイベントを1件発行する()
    {
        var (note, session, _, _) = await ConflictedNoteAsync("c-pub-local");
        await ExposeToSearchAsync(session, note.NoteId);
        var before = UpdatesFor(note.NoteId);
        before.Should().NotBeEmpty("陽性対照: 露出 ON で索引の生産側へ流れている");
        before[^1].ContentFingerprint.Should().Be(DocumentBodyIntake.Fingerprint("サーバ版"),
            "前提: 解決前に索引へ届いているのはサーバ版の本文");

        await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Local);

        var published = UpdatesFor(note.NoteId);
        published.Should().HaveCount(before.Count + 1, "本文を書き換えた解決は 1 回だけ再発行する");
        published[^1].ContentFingerprint.Should().Be(DocumentBodyIntake.Fingerprint("ローカル版"),
            "索引へ届くのはローカル側の新しい本文である（古い本文のまま残さない）");
    }

    [Fact]
    public async Task localの解決は露出が全てOFFの個人資料では発行しない()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-pub-off");

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Local);

        // 陽性対照: 解決は効いている（本文が書き換わり、版が進んだ）。
        body.NoteVersion.Should().Be(note.Version + 2);
        var pull = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        pull!.Content.Should().Be("ローカル版");

        UpdatesFor(note.NoteId).Should().BeEmpty(
            "3 つとも OFF の資料はイベントそのものを出さない（ADR-0061 決定 2）");
    }

    // `both` の別名資料は**新規作成**であり、作成経路と同じ既定（露出 3 トグル OFF）で作られる ——
    // **元の資料の露出を継がない**。門は作成経路（push）と同じ形で通すが、別名資料は索引可でないので
    // 発行されない。元の資料は 1 バイトも変わらないので再発行もしない。
    [Fact]
    public async Task bothの解決で作る別名資料は元の資料が露出ONでも露出OFFで作られ発行されない()
    {
        var ct = TestContext.Current.CancellationToken;
        var (note, session, _, _) = await ConflictedNoteAsync("c-pub-both");
        await ExposeToSearchAsync(session, note.NoteId);
        var sourceBefore = UpdatesFor(note.NoteId).Count;
        sourceBefore.Should().BePositive("陽性対照: 元の資料は露出 ON で索引の生産側へ流れている");

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Both);
        var createdId = body.CreatedNoteId!.Value;

        var list = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!;
        list.Notes.Single(n => n.Id == note.NoteId).IncludeInSearch.Should().BeTrue(
            "前提: 元の資料は露出 ON のまま");
        var created = list.Notes.Single(n => n.Id == createdId);
        created.IncludeInSearch.Should().BeFalse("別名資料は新規作成の既定で作られ、元の資料の露出を継がない");
        created.IncludeInGraph.Should().BeFalse();
        created.IncludeInAi.Should().BeFalse();

        UpdatesFor(createdId).Should().BeEmpty("露出 OFF の新規資料は索引の生産側へ流さない");
        UpdatesFor(note.NoteId).Should().HaveCount(sourceBefore, "元の資料は変わらないので再発行しない");
    }

    [Fact]
    public async Task bothの解決は露出が全てOFFの個人資料ではどちらの資料も発行しない()
    {
        var (note, session, _, plugin) = await ConflictedNoteAsync("c-pub-both-off");

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Both);

        // 陽性対照: 別名資料は作られ、ローカル本文が入っている。
        var created = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{body.CreatedNoteId}", TestContext.Current.CancellationToken);
        created!.Content.Should().Be("ローカル版");

        UpdatesFor(body.CreatedNoteId!.Value).Should().BeEmpty();
        UpdatesFor(note.NoteId).Should().BeEmpty();
    }

    // ── ADR-0105（#1498）: 別名資料が引き継ぐのはタグだけ ─────────────────
    //
    // 決定 3: タグは**引き継ぐ**（組織共通の辞書への参照で秘密性を持たない）。
    // 決定 1・2・3・4: 露出 3 トグル・共有先・版履歴・機密区分は**引き継がない**。
    // 🔴 **陰性は陽性対照と対で置く** —— 元の資料が露出 ON・共有あり・版 3 以上・機密区分が既定と違う状態を
    // 先に確かめないと、「そもそも元の資料に無かったから写らなかった」で緑になる。

    private HttpClient TagAdmin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");
        return client;
    }

    private async Task<int> UsageCountAsync(Guid tagId)
    {
        var body = await TagAdmin().GetFromJsonAsync<TagDictionaryResponse>("/tags",
            TestContext.Current.CancellationToken);
        return body!.Tags.Single(t => t.Id == tagId).UsageCount;
    }

    // 元の資料にタグ 2 つ・共有 1 件・既定と違う機密区分を与える（露出は呼び出し側が API で ON にしておく）。
    // 属性は現在の値（露出を含む）を土台に機密区分だけを差し替える —— 露出の投影を壊さない。
    private async Task<(Tag A, Tag B)> DecorateSourceAsync(Guid noteId, string grantedBy)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Tag.Create($"引継{Guid.NewGuid():N}"[..12]);
        var b = Tag.Create($"引継{Guid.NewGuid():N}"[..12]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.Tags.AddRange(a, b);
        var doc = await db.Documents.FirstAsync(d => d.Id == noteId, ct);
        var attributes = new Dictionary<string, string>(doc.Attributes)
        {
            [DocumentAttributes.ConfidentialityKey] = "internal",
        };
        doc.UpdateMetadata(attributes, [a.Id, b.Id], "test-decorate");
        db.DocumentShares.Add(DocumentShare.Create(noteId, "user", "colleague-1", grantedBy));
        await db.SaveChangesAsync(ct);
        return (a, b);
    }

    [Fact]
    public async Task bothの解決で作る別名資料は元の資料のタグを引き継ぎ使用件数が1増える()
    {
        var ct = TestContext.Current.CancellationToken;
        var (note, session, user, _) = await ConflictedNoteAsync("c-alias-tags");
        var (a, b) = await DecorateSourceAsync(note.NoteId, user);
        (await UsageCountAsync(a.Id)).Should().Be(1, "前提: 元の資料だけがタグを参照している");
        (await UsageCountAsync(b.Id)).Should().Be(1);

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Both);
        var createdId = body.CreatedNoteId!.Value;

        var list = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!;
        list.Notes.Single(n => n.Id == createdId).Tags.Should().BeEquivalentTo([a.Name, b.Name],
            "ADR-0105 決定 3: 別名資料は元の資料のタグを引き継ぐ");
        list.Notes.Single(n => n.Id == note.NoteId).Tags.Should().BeEquivalentTo([a.Name, b.Name],
            "元の資料のタグは変わらない");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var created = await db.Documents.FirstAsync(d => d.Id == createdId, ct);
            var source = await db.Documents.FirstAsync(d => d.Id == note.NoteId, ct);
            created.Tags.Should().Equal(source.Tags, "識別子の集合をそのまま写す（並びも保つ）");
            created.Tags.Should().NotBeSameAs(source.Tags, "元の資料と同じリストの実体を共有しない");
        }

        // ADR-0105 決定 3 が受け入れた副作用: 写しの分だけ使用件数が増える（写しがある間は削除できない）。
        (await UsageCountAsync(a.Id)).Should().Be(2);
        (await UsageCountAsync(b.Id)).Should().Be(2);
    }

    [Fact]
    public async Task bothの解決で作る別名資料は露出も共有先も版履歴も機密区分も引き継がない()
    {
        var ct = TestContext.Current.CancellationToken;
        var (note, session, user, plugin) = await ConflictedNoteAsync("c-alias-noinherit");
        await ExposeToSearchAsync(session, note.NoteId);
        await DecorateSourceAsync(note.NoteId, user);
        var sourceUpdates = UpdatesFor(note.NoteId).Count;

        // 陽性対照: 元の資料は露出 ON・共有 1 件・版 3 以上・機密区分が既定（restricted）と違う。
        var before = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!
            .Notes.Single(n => n.Id == note.NoteId);
        before.IncludeInSearch.Should().BeTrue("前提: 元の資料は露出 ON");
        before.SharedUserCount.Should().Be(1, "前提: 元の資料は 1 人に共有されている");
        before.Version.Should().BeGreaterThanOrEqualTo(3, "前提: 元の資料は複数の版を持つ");

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Both);
        var createdId = body.CreatedNoteId!.Value;

        var created = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!
            .Notes.Single(n => n.Id == createdId);
        // 決定 1・4: 露出 3 トグルは引き継がない（3 つとも OFF）。
        created.IncludeInSearch.Should().BeFalse();
        created.IncludeInGraph.Should().BeFalse();
        created.IncludeInAi.Should().BeFalse();
        // 決定 2: 共有先は引き継がない（共有されていない資料として始まる）。
        created.Visibility.Should().Be(PrivateNoteVisibilityValues.Private);
        created.SharedUserCount.Should().Be(0);
        created.SharedGroupCount.Should().Be(0);
        // 決定 3: 版履歴は引き継がない（1 版から始まる）。
        created.Version.Should().Be(1);
        var pulled = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{createdId}", ct);
        pulled!.Version.Should().Be(1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            (await db.DocumentShares.CountAsync(s => s.DocumentId == createdId, ct))
                .Should().Be(0, "共有台帳へ利用者の明示操作なしに行を足さない");
            (await db.DocumentShares.CountAsync(s => s.DocumentId == note.NoteId, ct))
                .Should().Be(1, "元の資料の共有はそのまま");
            (await db.DocumentVersions.CountAsync(v => v.DocumentId == createdId, ct))
                .Should().Be(1, "別名資料の版履歴は作成の 1 版だけ");
            var doc = await db.Documents.FirstAsync(d => d.Id == createdId, ct);
            // 決定 4: OFF は属性の不在ではなく資料単位の明示の値で書く。
            doc.Attributes.Should().ContainKey(DocumentAttributes.ConfidentialityKey)
                .WhoseValue.Should().Be("restricted", "機密区分は個人資料の既定へ戻る（元の資料は internal）");
            foreach (var key in DocumentExposure.AllKeys)
                doc.Attributes.Should().ContainKey(key)
                    .WhoseValue.Should().Be(DocumentExposure.Excluded, $"{key} は明示の OFF が書かれている");
        }

        UpdatesFor(createdId).Should().BeEmpty("露出 OFF の別名資料は索引の生産側へ流れない");
        UpdatesFor(note.NoteId).Should().HaveCount(sourceUpdates, "元の資料は変わらないので再発行しない");
    }

    [Fact]
    public async Task bothの解決はタグの無い資料では別名資料のタグも空になる()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, session, _, _) = await ConflictedNoteAsync("c-alias-notags");

        var body = await ResolveOnlyConflictAsync(session, SyncConflictResolutions.Both);

        var list = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!;
        list.Notes.Single(n => n.Id == body.CreatedNoteId).Tags.Should().BeEmpty();
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
