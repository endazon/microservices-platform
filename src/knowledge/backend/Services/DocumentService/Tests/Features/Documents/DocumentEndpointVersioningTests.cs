using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;
using Knowledge.Contracts.Dtos;
using DocumentService.Domain;

namespace DocumentService.Tests.Features.Documents;

// FR-06, UC-03: バージョン管理・メタデータ管理エンドポイントのテスト
[Trait("TestKind", "Integration")]
public class DocumentEndpointVersioningTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    // FR-05, IADR-0047: 機密区分はサーバー側で必須。作成/更新の既定フィクスチャ属性。
    private static Dictionary<string, string> Conf() => new() { ["confidentiality"] = "internal" };

    [Fact]
    public async Task CreateThenUpdate_VersionHistoryGrows()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "v1", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);
        doc!.Version.Should().Be(1);

        var update = await client.PutAsJsonAsync($"/documents/{doc.Id}",
            new { title = "v2", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);
        updated!.Version.Should().Be(2);

        var versionsResp = await client.GetAsync($"/documents/{doc.Id}/versions", TestContext.Current.CancellationToken);
        versionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await versionsResp.Content.ReadFromJsonAsync<List<DocumentVersionDto>>(
            TestContext.Current.CancellationToken);
        versions!.Count.Should().Be(2);
        // 新しい順
        versions[0].Version.Should().Be(2);
        versions[1].Version.Should().Be(1);
    }

    [Fact]
    public async Task GetSpecificVersion_ReturnsSnapshot()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "original", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "changed", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);

        var v1Resp = await client.GetAsync($"/documents/{doc.Id}/versions/1", TestContext.Current.CancellationToken);
        v1Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v1 = await v1Resp.Content.ReadFromJsonAsync<DocumentVersionDto>(TestContext.Current.CancellationToken);
        v1!.Title.Should().Be("original");

        var missing = await client.GetAsync($"/documents/{doc.Id}/versions/99", TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchMetadata_UpdatesAttributesOnly()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "keep-title", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        // SC-05, #635: **タグは辞書に在る名前しか付けられない**（手入力は自動登録しない。
        // [[IADR-0153]] 決定 5）。辞書へ先に登録する。
        var tagName = $"q3-{Guid.NewGuid():N}";
        (await client.PostAsJsonAsync("/tags", new CreateTagRequest(tagName), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var patch = await client.PatchAsJsonAsync($"/documents/{doc!.Id}/metadata",
            new { attributes = new Dictionary<string, string> { ["confidentiality"] = "internal", ["dept"] = "sales" }, tags = new[] { tagName } }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patch.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);
        patched!.Title.Should().Be("keep-title");
        patched.Attributes.Should().ContainKey("dept");
        // **応答は表示名で返る**（正本は識別子。[[IADR-0153]] 決定 2。契約は変わっていない）。
        patched.Tags.Should().Contain(tagName);
        patched.Version.Should().Be(2);
    }

    [Fact]
    public async Task Update_WithStaleExpectedVersion_Returns409()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "doc", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        // 現在版は 1。期待版 5 は不一致 → 409
        var conflict = await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "x", attributes = Conf(), tags = new List<string>(), expectedVersion = 5 }, TestContext.Current.CancellationToken);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_SetsStatusPublished()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "to-publish", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        var publish = await client.PostAsync($"/documents/{doc!.Id}/publish", null, TestContext.Current.CancellationToken);
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await publish.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);
        published!.Status.Should().Be(DocumentStatus.Published);
    }

    // SC-05, UC-03: アーカイブ済み文書の再公開は 409（不正遷移）。UI だけでなく API 側でも遷移を止める。
    [Fact]
    public async Task Publish_AfterArchive_Returns409()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "to-archive", attributes = Conf(), tags = new List<string>() }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);

        (await client.PostAsync($"/documents/{doc!.Id}/archive", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var republish = await client.PostAsync($"/documents/{doc.Id}/publish", null, TestContext.Current.CancellationToken);
        republish.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateWithBlankTitle_Returns400()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/documents",
            new { title = "", tags = new List<string>() }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- FR-06, FR-19, UC-03, #1011: 版応答は「版ごとの本文」を約束しない ----
    //
    // 計画 FR-06 の射程は版の作成・一覧・**取得**まで（［2026-08-23 明確化］・環流 planning#473）。
    // 版ごとの本文は保持されない —— それ自体は是正の対象ではないが、**保持していないものを
    // 保持しているかのように見せる契約**は誤りである。応答から本文の参照を落としたことを固定する。

    // 所有者つき・本文つきで作成し、本文を差し替えて版 2 を作る。
    private async Task<DocumentDto> CreateThenReplaceBodyAsync(
        string firstBody, string secondBody)
    {
        var client = Client();
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            // FR-21, ADR-0036 D-02: 本文の書き込みは所有者ベースの動的束縛で判定する。
            // 既定の主体（TestAuthHandler.DefaultUser）を所有者にして PUT を通す。
            ["owner"] = TestAuthHandler.DefaultUser,
        };

        var create = await client.PostAsJsonAsync("/documents",
            new { title = "版つき文書", body = firstBody, attributes, tags = new List<string>() },
            TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await create.Content.ReadFromJsonAsync<DocumentDto>(
            TestContext.Current.CancellationToken))!;
        doc.Version.Should().Be(1);

        var put = await client.PutAsJsonAsync($"/documents/{doc.Id}/body",
            new { body = secondBody }, TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await put.Content.ReadFromJsonAsync<DocumentDto>(
            TestContext.Current.CancellationToken))!;
        updated.Version.Should().Be(2);
        return updated;
    }

    // 🔴 **生 JSON で見る。** `DocumentVersionDto` で読むと、型に無いフィールドは
    // System.Text.Json が黙って捨てるため、サーバが返していてもテストは気づけない。
    [Fact]
    public async Task 版応答は本文の参照を含まない()
    {
        var doc = await CreateThenReplaceBodyAsync("版 1 の本文である。", "版 2 の本文である。");
        var client = Client();

        var single = await client.GetAsync($"/documents/{doc.Id}/versions/1",
            TestContext.Current.CancellationToken);
        single.StatusCode.Should().Be(HttpStatusCode.OK);
        var singleJson = await single.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var list = await client.GetAsync($"/documents/{doc.Id}/versions",
            TestContext.Current.CancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listJson = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // 応答が空でないこと（版そのものは返り続ける。版行を消したのではない）。
        singleJson.Should().Contain("\"version\":1").And.Contain("changeNote");
        listJson.Should().Contain("\"version\":2");

        // **本文の参照は入らない。** 大文字小文字を問わず出てはならない。
        singleJson.Should().NotContainEquivalentOf("markdownUri");
        listJson.Should().NotContainEquivalentOf("markdownUri");

        // 対照: **現行版の詳細には出る。** 「そもそも文書が本文を持っていない」ので
        // 出なかっただけ、という空振りの緑を排除する。
        var detail = await client.GetAsync($"/documents/{doc.Id}", TestContext.Current.CancellationToken);
        var detailJson = await detail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        detailJson.Should().ContainEquivalentOf("markdownUri");
    }

    // 機序の固定: キーは文書 ID だけで決まるため、再投入は同じキーを上書きする。
    // **版ごとの本文はどこにも残らない**（だから契約も約束しない）。
    [Fact]
    public async Task 版ごとの本文は保持されない()
    {
        const string first = "版 1 の本文である。";
        const string second = "版 2 の本文である。";
        var doc = await CreateThenReplaceBodyAsync(first, second);

        // キーは版に依らない（`DocumentBodyIntake.StorageKey` は文書 ID だけを受ける）。
        doc.MarkdownUri.Should().NotBeNull();
        doc.MarkdownUri.Should().EndWith($"documents/{doc.Id:D}/body.md");

        // 同じ参照から返るのは**最後の本文だけ**である。
        (await factory.Storage.GetTextAsync(doc.MarkdownUri!, TestContext.Current.CancellationToken))
            .Should().Be(second);
        factory.Storage.Texts.Values.Should().NotContain(first,
            "版 1 の本文は同じキーへ上書きされ、どの版からも引けない");
    }
}
