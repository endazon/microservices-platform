using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentService.Tests.Features.PrivateNotes;

// 🔴 FR-19, FR-20, UC-11, SC-03, SC-19, 計画 ADR-0142 決定 1〜4（2026-10-24 の決定 2 の補完を含む）, IADR-0636 (#2138):
// **論理削除した個人資料を見つかる面から外し、復元で元の露出のまま戻す。**
//
// 発行の側（索引・グラフの生産側へ流れるもの）と読み取りの側（SC-03・MCP が通る唯一の判定点）を、本番の端点で固定する。
// 受け手（取り込み・グラフ）が撤収の形を受けて実際に外すことは、それぞれの試験（`IngestionService.Tests` / `GraphService.Tests`）と
// 段間の結合（`Knowledge.IntegrationTests` の `IngestToSearchInProcessTests`）が測る。
//
// 陰性（出ない・読めない）は同じ試験の中で陽性対照（出る・読める）と対にする。
[Trait("TestKind", "Integration")]
public class PrivateNoteSoftDeleteWithdrawalTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. Bus.PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == documentId)];

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    // 1 回の試験の主体（所有者・共有先・第三者）。名前は試験ごとに一意にする（同じ器を共有するため）。
    private sealed record Actors(string Owner, string Shared, HttpClient Session, HttpClient Plugin);

    private async Task<Actors> ActorsAsync()
    {
        var owner = $"sd-{Guid.NewGuid():N}"[..20];
        var shared = $"sb-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(owner);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Actors(owner, shared, session, plugin);
    }

    private static async Task<Guid> PushAsync(HttpClient plugin, string path, string content)
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = path, title = path, edits = new[] { new { content } } }, Ct);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!.NoteId;
    }

    private static async Task ExposeAsync(HttpClient session, Guid noteId, bool search, bool graph, bool ai)
    {
        var res = await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure",
            new UpdateExposureRequest(search, graph, ai), Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task ShareAsync(HttpClient session, Guid noteId, string subject)
    {
        var share = await session.PostAsJsonAsync($"/documents/{noteId}/shares",
            new { subjectType = "user", subjectId = subject }, Ct);
        share.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 露出 ON・共有ありの資料（撤収の対象になる資料）。
    private async Task<(Actors A, Guid NoteId)> ExposedSharedNoteAsync(string path)
    {
        var a = await ActorsAsync();
        var noteId = await PushAsync(a.Plugin, path, "共有先の検索に出る本文");
        await ShareAsync(a.Session, noteId, a.Shared);
        await ExposeAsync(a.Session, noteId, search: true, graph: true, ai: false);
        return (a, noteId);
    }

    private static async Task<PrivateNoteDto> LedgerAsync(HttpClient session, Guid noteId)
    {
        var list = await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", Ct);
        return list!.Notes.Single(n => n.Id == noteId);
    }

    private static void ShouldBeWithdrawal(DocumentUpdated e)
    {
        e.Attributes.Should().Contain(DocumentScopes.Key, DocumentScopes.PrivateNote);
        foreach (var key in DocumentExposure.AllKeys)
            e.Attributes.Should().Contain(key, DocumentExposure.Excluded, $"撤収の形は露出 3 キーをすべて excluded にする（{key}）");
        DocumentExposure.IsIndexable(e.Attributes).Should().BeFalse("受け手（取り込み・グラフ）が同じ述語で撤収する");
    }

    // ── 発行の側（ADR-0142 決定 1・4） ─────────────────────────────────────────

    [Fact]
    public async Task SC19の削除で撤収の形が出て台帳の露出は保たれる()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("削除する.md");
        var before = UpdatesFor(noteId);
        DocumentExposure.IsIndexable(before[^1].Attributes).Should().BeTrue("陽性対照: 削除の前は索引へ載る形で出ている");

        (await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = UpdatesFor(noteId);
        after.Count.Should().Be(before.Count + 1, "削除 1 回で撤収のイベントを 1 件出す");
        ShouldBeWithdrawal(after[^1]);

        var ledger = await LedgerAsync(a.Session, noteId);
        ledger.Deleted.Should().BeTrue();
        ledger.IncludeInSearch.Should().BeTrue("台帳の露出は保つ（ADR-0142 決定 1。復元で使う）");
        ledger.IncludeInGraph.Should().BeTrue();
    }

    [Fact]
    public async Task 露出が全てOFFの資料は削除しても何も出ない()
    {
        var a = await ActorsAsync();
        var noteId = await PushAsync(a.Plugin, "出さない.md", "索引に無い本文");
        UpdatesFor(noteId).Should().BeEmpty("前提: 露出が全て OFF の資料は 1 度も索引へ流れていない");

        (await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Should().BeEmpty("索引に無いものを索引に無いまま保つ（ADR-0061 決定 2）");
        (await LedgerAsync(a.Session, noteId)).Deleted.Should().BeTrue("陽性対照: 削除そのものは確定している");
    }

    // 境界: 削除済みへの再削除（期限を延ばさない冪等）は何も出さない。
    [Fact]
    public async Task 削除済みへの再削除は何も出さない()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("二度消す.md");
        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);
        var afterFirst = UpdatesFor(noteId).Count;

        (await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Count.Should().Be(afterFirst);
    }

    [Fact]
    public async Task Obsidian側の削除でも撤収の形が出る()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("端末で消す.md");
        var before = UpdatesFor(noteId).Count;

        var deleted = await a.Plugin.PostAsync($"/private-notes/sync/notes/{noteId}/delete", null, Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = UpdatesFor(noteId);
        after.Count.Should().Be(before + 1);
        ShouldBeWithdrawal(after[^1]);
    }

    [Fact]
    public async Task 復元で台帳の露出のまま出し直す()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("戻す.md");
        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);
        ShouldBeWithdrawal(UpdatesFor(noteId)[^1]);

        (await a.Session.PostAsync($"/private-notes/{noteId}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var restored = UpdatesFor(noteId)[^1];
        restored.Attributes.Should().Contain(DocumentExposure.SearchKey, DocumentExposure.Included);
        restored.Attributes.Should().Contain(DocumentExposure.GraphKey, DocumentExposure.Included);
        restored.Attributes.Should().Contain(DocumentExposure.AiKey, DocumentExposure.Excluded, "削除前の値のまま（ON にしていない軸は OFF のまま）");
        restored.SharedWith.Should().Contain(a.Shared, "共有台帳は削除で変えない");
    }

    [Fact]
    public async Task 露出が全てOFFの資料は復元しても何も出ない()
    {
        var a = await ActorsAsync();
        var noteId = await PushAsync(a.Plugin, "戻しても出さない.md", "本文");
        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);

        (await a.Session.PostAsync($"/private-notes/{noteId}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Should().BeEmpty();
    }

    // 削除中に他の経路が門を通っても（共有の付与・取り消し）、発行は撤収の形になる。共有の変更そのものは許す（IADR-0636 決定 4）。
    [Fact]
    public async Task 削除中の共有の変更は許すが撤収の形でしか出ない()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("削除中に共有.md");
        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);
        var afterDelete = UpdatesFor(noteId).Count;

        await ShareAsync(a.Session, noteId, "carol-during-trash");
        (await a.Session.DeleteAsync($"/documents/{noteId}/shares/user/{a.Shared}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "暫定手段（共有の取り消し）は削除中も使える");

        var during = UpdatesFor(noteId).Skip(afterDelete).ToList();
        during.Should().HaveCount(2, "陽性対照: 共有の変更は従前どおり発行の契機である");
        during.Should().AllSatisfy(ShouldBeWithdrawal);
    }

    // ── 露出の変更（ADR-0142 決定 3） ───────────────────────────────────────

    [Fact]
    public async Task 削除中の露出の変更は409で台帳もイベントも変えない()
    {
        var a = await ActorsAsync();
        var noteId = await PushAsync(a.Plugin, "削除中に露出.md", "本文");
        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);

        var res = await a.Session.PutAsJsonAsync($"/private-notes/{noteId}/exposure",
            new UpdateExposureRequest(true, true, true), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("\"deleted\"");
        UpdatesFor(noteId).Should().BeEmpty("削除中に ON にして索引へ載る経路を作らない");
        var ledger = await LedgerAsync(a.Session, noteId);
        ledger.IncludeInSearch.Should().BeFalse();
        ledger.IncludeInGraph.Should().BeFalse();
        ledger.IncludeInAi.Should().BeFalse();

        // 陽性対照: 復元すれば露出を変えられる（409 は削除中だけ）。
        await a.Session.PostAsync($"/private-notes/{noteId}/restore", null, Ct);
        await ExposeAsync(a.Session, noteId, search: true, graph: false, ai: false);
    }

    // ── 読み取りの側（ADR-0142 決定 2。SC-03 と MCP の取得・一覧が同じ判定を通る） ──────────────

    [Fact]
    public async Task 削除中は所有者も共有先もSC03で開けず一覧にも出ず復元で戻る()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("開けなくする.md");
        var shared = SessionAs(a.Shared);

        // 陽性対照: 削除の前は所有者も共有先も開ける。
        (await shared.GetAsync($"/documents/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await a.Session.GetAsync($"/documents/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedIdsAsync(shared)).Should().Contain(noteId);

        await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct);

        foreach (var (who, client) in new[] { ("共有先", shared), ("所有者", a.Session) })
        {
            (await client.GetAsync($"/documents/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"{who}: 存在ごと秘匿（ADR-0056）");
            (await client.GetAsync($"/documents/{noteId}/versions", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, who);
            (await client.GetAsync($"/documents/{noteId}/versions/1", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, who);
            (await ListedIdsAsync(client)).Should().NotContain(noteId, who);
        }
        (await LedgerAsync(a.Session, noteId)).Deleted.Should().BeTrue("所有者は SC-19 の削除済みタブでは見える");

        await a.Session.PostAsync($"/private-notes/{noteId}/restore", null, Ct);

        (await shared.GetAsync($"/documents/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "復元の後は共有先も再び開ける（決定 4）");
        (await ListedIdsAsync(shared)).Should().Contain(noteId);
    }

    private static async Task<List<Guid>> ListedIdsAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct);
        return [.. list!.Select(d => d.Id)];
    }

    // ── 発行失敗の注入 ──────────────────────────────────────────────────────

    // 確定（トランザクションの開始・コミット）が失敗したら、削除の印も残らない。🔴 削除の保存を確定の外（`SaveChangesAsync` の直呼び）へ
    // 出すと、撤収のイベントが出ないまま台帳だけが削除中になり（＝索引に残ったまま「削除済み」と見える）、ここで赤になる。
    private sealed class CommitFailingPublisher : IDocumentUpdatedPublisher
    {
        public Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
            => throw new InvalidOperationException("確定の失敗（試験の注入）");

        public Task PublishUpdatedAsync(Guid documentId, string title, string status, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
            string? contentFingerprint = null, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            List<string>? sharedWith = null, CancellationToken ct = default)
            => throw new InvalidOperationException("確定の外では積めない");
    }

    [Fact]
    public async Task 確定が失敗すれば削除の印も残らず撤収も出ない()
    {
        var (a, noteId) = await ExposedSharedNoteAsync("確定に失敗する.md");
        var before = UpdatesFor(noteId).Count;
        using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDocumentUpdatedPublisher>();
            services.AddScoped<IDocumentUpdatedPublisher, CommitFailingPublisher>();
        }));
        var session = failing.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, a.Owner);

        // TestServer は端点の未処理例外を呼び出し側へそのまま投げる（確定していない削除を成功と返さない）。
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DeleteAsync($"/private-notes/{noteId}", Ct));
        thrown.Message.Should().Contain("試験の注入");

        (await LedgerAsync(a.Session, noteId)).Deleted.Should().BeFalse("確定が失敗すれば削除の印も残らない");
        UpdatesFor(noteId).Count.Should().Be(before);
        // 陽性対照: 同じ資料は正常な確定で削除でき、撤収が出る。
        (await a.Session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldBeWithdrawal(UpdatesFor(noteId)[^1]);
    }
}
