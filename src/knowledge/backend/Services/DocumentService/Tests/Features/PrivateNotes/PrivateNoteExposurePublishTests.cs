using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, FR-21 受け入れ基準 ⑨, UC-11, SC-19, SC-20, ADR-0061 決定 1・2・4・5, [[IADR-0396]]:
// **露出 3 トグルを索引の生産側へ配線した「発行の門」を固定する。**
//
// 計画（planning#492 → `ADR-0061`）の裁定:
//   1. 1 つでも ON なら索引へ載せる  2. 3 つとも OFF なら載せない
//   4. ON → OFF は索引からの削除まで及ぶ  5. 判定軸に `doc_scope` / `owner` / `shared_with` を含む
//
// 🔴 **陰性（発行されない）の主張には陽性対照を対で置く。** 「イベントが 1 件も出ていない」は
// 配線が丸ごと壊れていても真になるため、同じテストの中で**出るはずのものが出ている**ことを示す。
[Trait("TestKind", "Integration")]
public class PrivateNoteExposurePublishTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. Bus.PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == documentId)];

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync()
    {
        var user = $"expo-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" });
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>())!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, session, plugin);
    }

    private static async Task<Guid> PushAsync(HttpClient plugin, string path, string content)
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = path, title = path, edits = new[] { new { content } } });
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>())!.NoteId;
    }

    private static async Task<PrivateNoteDto> SetExposureAsync(HttpClient session, Guid noteId,
        bool search, bool graph, bool ai)
    {
        var res = await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure",
            new UpdateExposureRequest(search, graph, ai));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<PrivateNoteDto>())!;
    }

    // 受け入れ基準 1: **3 トグルが OFF のあいだ、本文の作成・更新は `DocumentUpdated` を発行しない。**
    // （ADR-0061 決定 2「既定は『索引に存在しない』ことで構造的に守る」）
    [Fact]
    public async Task 露出が全てOFFのあいだ本文を書いてもイベントは発行されない()
    {
        var (_, _, plugin) = await OwnerAsync();

        var noteId = await PushAsync(plugin, "秘密.md", "全 OFF の本文");
        var update = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId,
            vaultPath = "秘密.md",
            title = "秘密.md",
            baseVersion = 1,
            edits = new[] { new { content = "全 OFF の本文（更新）" } },
        }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Should().BeEmpty(
            "既定 OFF の個人資料は索引の生産側へ 1 度も流れない（ADR-0061 決定 2）");
    }

    // 受け入れ基準 2 / 5: **「横断検索に含める」を ON にすると発行され、判定軸が全部載っている。**
    //
    // 🔴 これが直前のテストの**陽性対照**である —— 同じ経路・同じ資料で「出る」ことを示さないと、
    // 「出ない」は配線が丸ごと死んでいても通る。
    [Fact]
    public async Task 横断検索をONにすると判定軸を載せたイベントが発行される()
    {
        var (user, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, "公開する.md", "検索に載せたい本文");

        // ADR-0036 D-06: 所有者が明示的に共有する（判定軸の第 3 節 `shared_with`）。
        var share = await session.PostAsJsonAsync($"/documents/{noteId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken);
        share.StatusCode.Should().Be(HttpStatusCode.Created);

        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);

        var published = UpdatesFor(noteId);
        published.Should().NotBeEmpty("露出 ON は索引の生産側へ流す（ADR-0061 決定 1）");

        var last = published[^1];
        last.Attributes.Should().Contain(DocumentScopes.Key, DocumentScopes.PrivateNote);
        last.Attributes.Should().Contain("owner", user);
        last.Attributes.Should().Contain(DocumentExposure.SearchKey, DocumentExposure.Included);
        last.Attributes.Should().Contain(DocumentExposure.GraphKey, DocumentExposure.Excluded);
        last.Attributes.Should().Contain(DocumentExposure.AiKey, DocumentExposure.Excluded);
        last.SharedWith.Should().Contain("bob",
            "`shared_with` は属性辞書では運べない。運ばないと共有先ベースの分岐が索引の側で成立しない");
    }

    // 受け入れ基準 4 の前提: **共有の付与・取り消しそのものが再発行の契機である。**
    // 再発行しないと、索引が運ぶ共有先は**発行時点の写しのまま**固まる ——
    // 付与は「共有した相手に永久に見えない」、取り消しは「取り消した相手に見え続ける」（漏れる向き）。
    [Fact]
    public async Task 共有の取り消しは索引へ届く形で再発行される()
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, "共有の取り消し.md", "本文");
        await session.PostAsJsonAsync($"/documents/{noteId}/shares",
            new { subjectType = "user", subjectId = "carol" }, TestContext.Current.CancellationToken);
        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);

        UpdatesFor(noteId)[^1].SharedWith.Should().Contain("carol", "陽性対照: 付与は届いている");

        var revoked = await session.DeleteAsync($"/documents/{noteId}/shares/user/carol",
            TestContext.Current.CancellationToken);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        UpdatesFor(noteId)[^1].SharedWith.Should().NotContain("carol",
            "取り消しの未反映は漏れる向きの乖離である");
    }

    // 受け入れ基準 6: **全 OFF へ戻すと、撤収のためのイベントが出る。**
    //
    // 索引からの実際の削除は受け手（`IngestionService` / `GraphService`）が行う ——
    // ここで固定するのは「**出るべきものが出ている**」ことである。出さなければ
    // 「属性で弾く」以前に、索引の中身が古い露出のまま取り残される。
    [Fact]
    public async Task 全てOFFへ戻すと撤収のためのイベントが発行される()
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, "戻す.md", "本文");
        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);
        var afterOn = UpdatesFor(noteId).Count;

        await SetExposureAsync(session, noteId, search: false, graph: false, ai: false);

        var published = UpdatesFor(noteId);
        published.Count.Should().BeGreaterThan(afterOn,
            "ON → OFF は索引からの削除まで及ぶ（ADR-0061 決定 4）。イベントが出ないと撤収の契機が無い");
        published[^1].Attributes.Should()
            .Contain(DocumentExposure.SearchKey, DocumentExposure.Excluded);
    }

    // 全 OFF のまま全 OFF を保存しても何も出さない（決定 2 の「存在しないまま保つ」）。
    // 上のテストと対になっている —— 片方だけだと「常に出す」実装でも通ってしまう。
    [Fact]
    public async Task 全てOFFのまま保存しても発行されない()
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, "変えない.md", "本文");

        await SetExposureAsync(session, noteId, search: false, graph: false, ai: false);

        UpdatesFor(noteId).Should().BeEmpty();
    }

    // 受け入れ基準 7: **露出トグルだけを変えても版は進まない**（[[IADR-0283]] 決定 4 を維持）。
    // 版が進むと版履歴が編集以外で膨らみ、Obsidian 同期の `baseVersion` が動いて 409 になる。
    [Fact]
    public async Task 露出の変更では版が進まない()
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, "版.md", "本文");
        var before = await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/",
            TestContext.Current.CancellationToken);
        var versionBefore = before!.Notes.Single(n => n.Id == noteId).Version;

        var dto = await SetExposureAsync(session, noteId, search: true, graph: true, ai: true);

        dto.Version.Should().Be(versionBefore);
    }

    // ［#1471］管理者の保存（`PUT /documents/{id}` / `PATCH /documents/{id}/metadata`）。
    // 既定の主体は `platform-admin`（`TestAuthHandler`）。**属性は全置換である。**
    private Task<HttpResponseMessage> AdminSaveAsync(string method, Guid id, string title,
        Dictionary<string, string> attributes)
    {
        var admin = factory.CreateClient();
        return method == "PUT"
            ? admin.PutAsJsonAsync($"/documents/{id}",
                new { title, attributes, tags = new List<string>() }, TestContext.Current.CancellationToken)
            : admin.PatchAsJsonAsync($"/documents/{id}/metadata",
                new { attributes, tags = new List<string>() }, TestContext.Current.CancellationToken);
    }

    // ［#1471］受け入れ基準: **管理者の属性全置換で露出が外れた個人資料は、撤収のイベントが出る**
    // （ADR-0061 決定 4 / [[IADR-0396]] 決定 5 / [[IADR-0455]] 決定 1）。
    //
    // 🔴 SetExposure だけが ON → OFF を作るのではない。属性の全置換で露出キーを `excluded` にする保存も
    // 同じ遷移であり、「今」索引可のときだけ出す単純な門を当てると**消させるためのイベントが弾かれて
    // 本文が索引に残る**。PR #1281 のレビュー修正（`c4830568`）をそのまま当てると、ここが落ちる。
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task 管理者の属性更新で露出が外れると撤収のイベントが発行される(string method)
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, $"管理者更新-{method}.md", "本文");
        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);

        var afterOn = UpdatesFor(noteId);
        afterOn.Should().NotBeEmpty("陽性対照: 露出 ON で索引の生産側へ流れている");
        var withdrawn = new Dictionary<string, string>(afterOn[^1].Attributes)
        {
            [DocumentExposure.SearchKey] = DocumentExposure.Excluded,
        };

        var res = await AdminSaveAsync(method, noteId, afterOn[^1].Title, withdrawn);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var published = UpdatesFor(noteId);
        published.Count.Should().BeGreaterThan(afterOn.Count,
            "ON → OFF は索引からの削除まで及ぶ。管理者の保存でもイベントが出ないと撤収の契機が無い");
        published[^1].Attributes.Should()
            .Contain(DocumentExposure.SearchKey, DocumentExposure.Excluded);
    }

    // ［#1471］上のテストと対: **全 OFF のまま管理者が保存しても発行しない**（ADR-0061 決定 2）。
    // 片方だけだと「管理者の保存は常に出す」実装（＝門を通さない直接発行）でも通ってしまう。
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task 全てOFFの個人資料を管理者が更新しても発行されない(string method)
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, $"管理者更新OFF-{method}.md", "本文");
        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);
        await SetExposureAsync(session, noteId, search: false, graph: false, ai: false);

        var afterOff = UpdatesFor(noteId);
        afterOff[^1].Attributes.Should().Contain(DocumentExposure.SearchKey, DocumentExposure.Excluded,
            "陽性対照: 撤収のイベントまでは出ている（ここから全 OFF のまま保存する）");

        var res = await AdminSaveAsync(method, noteId, afterOff[^1].Title,
            new Dictionary<string, string>(afterOff[^1].Attributes));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Count.Should().Be(afterOff.Count,
            "3 つとも OFF の資料はイベントそのものを出さない（索引に存在しないまま保つ）");
    }

    // ［#1471］陽性対照（組織文書）: **露出キーを明示的に全 `excluded` にした組織文書でも、発行は止まらない**
    // （[[IADR-0455]] 決定 2）。
    //
    // `DocumentExposure.IsAllowed` は明示値を文書種別より優先するので、この文書の `IsIndexable` は偽である。
    // 門を `IsIndexable` だけで書くと作成もアーカイブも発行されず、**WikiService へアーカイブ（ページの
    // 非公開化）が届かない**。門は個人資料にだけ効かせ、組織文書の挙動をデータに依らず不変に保つ。
    [Fact]
    public async Task 露出キーを全てexcludedにした組織文書でも作成とアーカイブは発行される()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = factory.CreateClient();
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            [DocumentExposure.SearchKey] = DocumentExposure.Excluded,
            [DocumentExposure.GraphKey] = DocumentExposure.Excluded,
            [DocumentExposure.AiKey] = DocumentExposure.Excluded,
        };
        DocumentExposure.IsIndexable(attributes).Should().BeFalse(
            "前提: 門を IsIndexable だけで書くと、この組織文書は止まる");

        var created = await admin.PostAsJsonAsync("/documents",
            new { title = $"露出キー付き組織文書 {Guid.NewGuid():N}", attributes, tags = new List<string>() }, ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await created.Content.ReadFromJsonAsync<DocumentDto>(ct))!;
        UpdatesFor(doc.Id).Should().NotBeEmpty("組織文書の作成は従来どおり発行される");

        var archived = await admin.PostAsync($"/documents/{doc.Id}/archive", null, ct);
        archived.StatusCode.Should().Be(HttpStatusCode.OK);
        UpdatesFor(doc.Id).Should().Contain(e => e.Status == "archived",
            "アーカイブが届かないと Wiki.js のページが公開のまま残る");
    }
}
