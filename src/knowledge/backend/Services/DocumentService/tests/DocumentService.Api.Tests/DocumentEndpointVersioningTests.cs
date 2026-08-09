using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using Knowledge.Contracts.Dtos;
using DocumentService.Api.Foundation.Domain;

namespace DocumentService.Api.Tests;

// FR-06, UC-03: バージョン管理・メタデータ管理エンドポイントのテスト
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
            new { title = "v1", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();
        doc!.Version.Should().Be(1);

        var update = await client.PutAsJsonAsync($"/documents/{doc.Id}",
            new { title = "v2", attributes = Conf(), tags = new List<string>() });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<DocumentDto>();
        updated!.Version.Should().Be(2);

        var versionsResp = await client.GetAsync($"/documents/{doc.Id}/versions");
        versionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await versionsResp.Content.ReadFromJsonAsync<List<DocumentVersionDto>>();
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
            new { title = "original", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "changed", attributes = Conf(), tags = new List<string>() });

        var v1Resp = await client.GetAsync($"/documents/{doc.Id}/versions/1");
        v1Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v1 = await v1Resp.Content.ReadFromJsonAsync<DocumentVersionDto>();
        v1!.Title.Should().Be("original");

        var missing = await client.GetAsync($"/documents/{doc.Id}/versions/99");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchMetadata_UpdatesAttributesOnly()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "keep-title", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        // SC-05, #635: **タグは辞書に在る名前しか付けられない**（手入力は自動登録しない。
        // [[IADR-0153]] 決定 5）。辞書へ先に登録する。
        var tagName = $"q3-{Guid.NewGuid():N}";
        (await client.PostAsJsonAsync("/tags", new CreateTagRequest(tagName)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var patch = await client.PatchAsJsonAsync($"/documents/{doc!.Id}/metadata",
            new { attributes = new Dictionary<string, string> { ["confidentiality"] = "internal", ["dept"] = "sales" }, tags = new[] { tagName } });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patch.Content.ReadFromJsonAsync<DocumentDto>();
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
            new { title = "doc", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        // 現在版は 1。期待版 5 は不一致 → 409
        var conflict = await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "x", attributes = Conf(), tags = new List<string>(), expectedVersion = 5 });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_SetsStatusPublished()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "to-publish", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        var publish = await client.PostAsync($"/documents/{doc!.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await publish.Content.ReadFromJsonAsync<DocumentDto>();
        published!.Status.Should().Be(DocumentStatus.Published);
    }

    // SC-05, UC-03: アーカイブ済み文書の再公開は 409（不正遷移）。UI だけでなく API 側でも遷移を止める。
    [Fact]
    public async Task Publish_AfterArchive_Returns409()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "to-archive", attributes = Conf(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        (await client.PostAsync($"/documents/{doc!.Id}/archive", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var republish = await client.PostAsync($"/documents/{doc.Id}/publish", null);
        republish.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateWithBlankTitle_Returns400()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/documents",
            new { title = "", tags = new List<string>() });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
