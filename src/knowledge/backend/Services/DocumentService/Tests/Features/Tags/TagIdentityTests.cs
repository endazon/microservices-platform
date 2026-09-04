using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Tags;

// FR-06, FR-09, SC-05, SC-09, UC-03, #635: タグの識別子保持と改名・削除（IADR-0153）。
//
// SC-09 が確定した「**改名は許して既存の文書が新しい名前へ追随する**」は、
// **正本が識別子を持つことでしか満たせない**——表示名を複写していると、改名しても
// 既存文書は古い名前を持ち続ける（複写を全件書き換える経路が別に要る）。
// ここで固定するのは、その追随が**文書を 1 件も書き換えずに**起きることである。
[Trait("TestKind", "Integration")]
public class TagIdentityTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 共有 DB のため、各テストは自分のタグ名を使う。
    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static Dictionary<string, string> Conf() => new() { ["confidentiality"] = "internal" };

    private async Task<TagDto> AddTagAsync(string name)
    {
        var resp = await Client().PostAsJsonAsync("/tags", new CreateTagRequest(name));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<TagDto>())!;
    }

    private async Task<DocumentDto> AddDocumentAsync(string title, params string[] tags)
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title, attributes = Conf(), tags });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    // 受け入れ基準: **辞書に無い名前は保存できない**（SC-05「既定タグ辞書に整合」）。
    //
    // **黙って落とさない。** 落とすと「保存できたのにタグが付いていない」という、
    // 利用者から見て説明のつかない結果になる。
    [Fact]
    public async Task Create_WithUnknownTag_Returns400()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "未知タグ", attributes = Conf(), tags = new[] { UniqueName("未登録") } }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchMetadata_WithUnknownTag_Returns400()
    {
        var doc = await AddDocumentAsync("既存文書");

        var resp = await Client().PatchAsJsonAsync($"/documents/{doc.Id}/metadata",
            new { attributes = Conf(), tags = new[] { UniqueName("未登録") } }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // **契約は表示名のままである**（[[IADR-0153]] 決定 2）。
    // 正本を識別子へ移しても、要求も応答も表示名で行き来する——下流も画面も変わらない。
    [Fact]
    public async Task Roundtrip_RequestAndResponse_UseDisplayNames()
    {
        var name = UniqueName("往復");
        await AddTagAsync(name);

        var doc = await AddDocumentAsync("往復文書", name);

        doc.Tags.Should().Equal([name]);
        var fetched = await Client().GetFromJsonAsync<DocumentDto>($"/documents/{doc.Id}", TestContext.Current.CancellationToken);
        fetched!.Tags.Should().Equal([name], "取得しても表示名で返る");
    }

    // ★ 受け入れ基準の中心: **改名すると既存文書の表示が追随する。**
    //
    // **文書は 1 件も書き換えない**——正本が識別子を持つため、変わるのは解決結果だけである。
    // **版も増えない**（[[IADR-0153]] 決定 3。改名は文書の内容変更ではない）。
    [Fact]
    public async Task Rename_ExistingDocumentsFollow_WithoutVersionBump()
    {
        var before = UniqueName("旧名");
        var after = UniqueName("新名");
        var tag = await AddTagAsync(before);
        var doc = await AddDocumentAsync("追随する文書", before);
        doc.Version.Should().Be(1);

        var resp = await Client().PutAsJsonAsync($"/tags/{tag.Id}", new RenameTagRequest(after), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var renamed = await resp.Content.ReadFromJsonAsync<RenameTagResponse>(TestContext.Current.CancellationToken);
        renamed!.Tag.Id.Should().Be(tag.Id, "識別子は改名で変わらない");
        renamed.Tag.Name.Should().Be(after);
        renamed.RepublishedDocuments.Should().Be(1, "射影を作り直すため該当文書だけ再発行する");

        var fetched = await Client().GetFromJsonAsync<DocumentDto>($"/documents/{doc.Id}", TestContext.Current.CancellationToken);
        fetched!.Tags.Should().Equal([after], "既存文書が新しい名前へ追随する");
        fetched.Version.Should().Be(1, "改名で版は増えない");

        // **射影（Qdrant / Wiki.js）は表示名を焼き込んだ複写なので、作り直す必要がある。**
        // 正本の追随（上の 2 行）だけでは検索結果と Wiki.js は古い名前のままになる（[[IADR-0153]] 決定 3）。
        // E3b: DocumentUpdated の発行は Wolverine（RecordingMessageBus で観測する）。
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        bus.PublishedOf<DocumentUpdated>().Should()
            .Contain(e => e.DocumentId == doc.Id && e.Tags.Contains(after),
                "新しい名前を載せた DocumentUpdated が再発行される");
    }

    // **改名したタグを使っていない文書は再発行しない。**
    // 辞書の 1 語の変更で索引全体が再構築されると、規模に比例して費用が出る。
    [Fact]
    public async Task Rename_DoesNotRepublishUnrelatedDocuments()
    {
        var used = UniqueName("使用");
        var unused = UniqueName("未使用");
        var usedTag = await AddTagAsync(used);
        await AddTagAsync(unused);
        var related = await AddDocumentAsync("関係あり", used);
        var unrelated = await AddDocumentAsync("関係なし", unused);

        var resp = await Client().PutAsJsonAsync($"/tags/{usedTag.Id}",
            new RenameTagRequest(UniqueName("使用新")), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<RenameTagResponse>(TestContext.Current.CancellationToken))!
            .RepublishedDocuments.Should().Be(1);

        // E3b: DocumentUpdated の発行は Wolverine（RecordingMessageBus で観測する）。
        var published = factory.Services.GetRequiredService<RecordingMessageBus>()
            .PublishedOf<DocumentUpdated>();
        published.Count(e => e.DocumentId == unrelated.Id)
            .Should().Be(1, "作成時の 1 通だけで、改名による再発行は無い");
        published.Count(e => e.DocumentId == related.Id)
            .Should().Be(2, "作成時 ＋ 改名の再発行");
    }

    // **過去版も新しい名前で表示される**（[[IADR-0153]] 決定 4）。
    // 版履歴も識別子を持つため、改名は履歴の表示にも一様に効く。
    [Fact]
    public async Task Rename_VersionHistoryAlsoShowsNewName()
    {
        var before = UniqueName("履歴旧");
        var after = UniqueName("履歴新");
        var tag = await AddTagAsync(before);
        var doc = await AddDocumentAsync("履歴文書", before);

        // 版 2 を作る（タグは付けたまま）。
        (await Client().PatchAsJsonAsync($"/documents/{doc.Id}/metadata",
            new { attributes = Conf(), tags = new[] { before }, changeNote = "見直し" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Client().PutAsJsonAsync($"/tags/{tag.Id}", new RenameTagRequest(after), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var versions = await Client()
            .GetFromJsonAsync<List<DocumentVersionDto>>($"/documents/{doc.Id}/versions", TestContext.Current.CancellationToken);
        versions!.Should().HaveCount(2);
        versions.Should().OnlyContain(v => v.Tags.Contains(after),
            "過去版も現在の表示名で出る（改名は表示上の変更である）");
    }

    // SC-09「新しい名前は既存値と重複しない」。
    [Fact]
    public async Task Rename_ToExistingName_Returns409()
    {
        var a = await AddTagAsync(UniqueName("A"));
        var bName = UniqueName("B");
        await AddTagAsync(bName);

        var resp = await Client().PutAsJsonAsync($"/tags/{a.Id}", new RenameTagRequest(bName), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // **自分自身への改名は 409 にしない**——実質の no-op を弾いても利用者は何も直せない。
    [Fact]
    public async Task Rename_ToSameName_IsAllowed()
    {
        var name = UniqueName("同名");
        var tag = await AddTagAsync(name);

        var resp = await Client().PutAsJsonAsync($"/tags/{tag.Id}", new RenameTagRequest($"  {name}  "), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-09 のアクセス制御: 改名・削除は**システム管理者限定**（読み取りは運用者にも開く）。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("viewer")]
    public async Task Rename_NonAdmin_IsForbidden(string role)
    {
        var tag = await AddTagAsync(UniqueName("権限"));

        var resp = await ClientAs(role).PutAsJsonAsync($"/tags/{tag.Id}",
            new RenameTagRequest(UniqueName("権限新")), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("platform-operator")]
    [InlineData("viewer")]
    public async Task Delete_NonAdmin_IsForbidden(string role)
    {
        var tag = await AddTagAsync(UniqueName("削除権限"));

        (await ClientAs(role).DeleteAsync($"/tags/{tag.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 受け入れ基準: **使用件数 0 件のときだけ削除できる**（[[IADR-0153]] 決定 6）。
    [Fact]
    public async Task Delete_UnusedTag_Succeeds()
    {
        var tag = await AddTagAsync(UniqueName("未使用削除"));

        (await Client().DeleteAsync($"/tags/{tag.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var body = await Client().GetFromJsonAsync<TagDictionaryResponse>("/tags", TestContext.Current.CancellationToken);
        body!.Tags.Should().NotContain(t => t.Id == tag.Id);
    }

    // **参照が 1 件でもあれば削除拒否**。SC-09 は「削除前に使用件数を示す」とも定めているので、
    // **件数を添えて返す**——数だけでも「まず 2 件を外してから消す」と管理者が行動できる。
    [Fact]
    public async Task Delete_UsedTag_Returns409_WithUsageCount()
    {
        var name = UniqueName("使用中");
        var tag = await AddTagAsync(name);
        await AddDocumentAsync("使用文書1", name);
        await AddDocumentAsync("使用文書2", name);

        var resp = await Client().DeleteAsync($"/tags/{tag.Id}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(
            TestContext.Current.CancellationToken);
        body!["usageCount"].GetInt32().Should().Be(2);
    }

    // **削除の判定と一覧の使用件数は同じ母集合で数える**（`LoadWithUsageAsync` と同じ定義）。
    // 一覧が 0 件と示したのに削除が 409 になると、管理者は辞書を信用できなくなる。
    [Fact]
    public async Task Delete_TagOnlyInVersionHistory_Succeeds()
    {
        var name = UniqueName("履歴のみ");
        var tag = await AddTagAsync(name);
        var doc = await AddDocumentAsync("付け替え文書", name);

        // 版 2 でタグを外す。版履歴には版 1 のタグが残る。
        (await Client().PatchAsJsonAsync($"/documents/{doc.Id}/metadata",
            new { attributes = Conf(), tags = Array.Empty<string>(), changeNote = "タグを外した" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Client().DeleteAsync($"/tags/{tag.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent,
                "版履歴を数えると、一度でも使われたタグを永久に削除できなくなる");
    }

    [Fact]
    public async Task Rename_UnknownId_Returns404()
    {
        var resp = await Client().PutAsJsonAsync($"/tags/{Guid.NewGuid()}",
            new RenameTagRequest(UniqueName("存在しない")), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        (await Client().DeleteAsync($"/tags/{Guid.NewGuid()}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
