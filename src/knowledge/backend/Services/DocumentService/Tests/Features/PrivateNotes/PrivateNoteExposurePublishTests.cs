using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
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

    // ［#1629］（旧 #1471 の 2 本を置き換えた）**管理者の保存は他人の個人資料に届かない。**
    //
    // #1471 は「管理者の属性全置換で露出が外れた個人資料は撤収のイベントが出る」をここで固定していたが、
    // その前提（管理者が他人の個人資料を書き換えられる）そのものが ADR-0036 D-08・ADR-0119 決定 3 の違反だった
    // （#1629）。管理の書き込み口は個人資料を主体に依らず対象外（404）とし、ON → OFF の撤収は所有者の
    // SetExposure（上の `全てOFFへ戻すと撤収のためのイベントが発行される`）が担う。
    //
    // 🔴 **陰性（404・イベント無し・属性不変）は陽性対照と対で置く**: 同じ資料で、露出 ON の発行が出ていること、
    // 所有者の SetExposure なら撤収まで届くことを同じテストの中で示す（「常に 404」の実装でも陰性だけは緑になる）。
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task 管理者の属性更新は他人の個人資料に届かず_404で撤収も書き換えも起きない(string method)
    {
        var (_, session, plugin) = await OwnerAsync();
        var noteId = await PushAsync(plugin, $"管理者更新-{method}.md", "本文");
        await SetExposureAsync(session, noteId, search: true, graph: false, ai: false);

        var afterOn = UpdatesFor(noteId);
        afterOn.Should().NotBeEmpty("陽性対照: 露出 ON で索引の生産側へ流れている");
        var withdrawn = new Dictionary<string, string>(afterOn[^1].Attributes)
        {
            [DocumentExposure.SearchKey] = DocumentExposure.Excluded,
            ["owner"] = TestAuthHandler.DefaultUser,
        };

        var res = await AdminSaveAsync(method, noteId, "管理者が付けた題名", withdrawn);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "個人資料は管理の口の対象外（存在を明かさない）");
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(noteId.ToString())
            .And.NotContain("管理者更新", "応答に表題・owner を出さない");

        UpdatesFor(noteId).Count.Should().Be(afterOn.Count, "書き換えていないので発行もしない");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var stored = (await db.Documents.FindAsync([noteId], TestContext.Current.CancellationToken))!;
            stored.Title.Should().NotBe("管理者が付けた題名", "管理者の保存は表題を変えていない");
            stored.Attributes.Should().Contain("owner", afterOn[^1].Attributes["owner"], "owner を奪えない");
            stored.Attributes.Should().NotContain(DocumentExposure.SearchKey, DocumentExposure.Excluded);
        }

        // 陽性対照: 所有者の経路（SetExposure）なら撤収まで届く ＝ 資料も配線も生きている。
        await SetExposureAsync(session, noteId, search: false, graph: false, ai: false);
        var afterOff = UpdatesFor(noteId);
        afterOff.Count.Should().BeGreaterThan(afterOn.Count, "陽性対照: 所有者の撤収は届く");
        afterOff[^1].Attributes.Should().Contain("owner", afterOn[^1].Attributes["owner"],
            "管理者の保存は owner を書き換えていない");
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
