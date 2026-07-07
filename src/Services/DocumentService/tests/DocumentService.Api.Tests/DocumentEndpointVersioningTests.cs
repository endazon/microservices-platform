using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;
using DocumentService.Api.Foundation.Domain;

namespace DocumentService.Api.Tests;

// FR-06, UC-03: バージョン管理・メタデータ管理エンドポイントのテスト
public class DocumentEndpointVersioningTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    [Fact]
    public async Task CreateThenUpdate_VersionHistoryGrows()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "v1", attributes = new Dictionary<string, string>(), tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();
        doc!.Version.Should().Be(1);

        var update = await client.PutAsJsonAsync($"/documents/{doc.Id}",
            new { title = "v2", attributes = new Dictionary<string, string>(), tags = new List<string>() });
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
            new { title = "original", tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "changed", tags = new List<string>() });

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
            new { title = "keep-title", tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        var patch = await client.PatchAsJsonAsync($"/documents/{doc!.Id}/metadata",
            new { attributes = new Dictionary<string, string> { ["dept"] = "sales" }, tags = new[] { "q3" } });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patch.Content.ReadFromJsonAsync<DocumentDto>();
        patched!.Title.Should().Be("keep-title");
        patched.Attributes.Should().ContainKey("dept");
        patched.Tags.Should().Contain("q3");
        patched.Version.Should().Be(2);
    }

    [Fact]
    public async Task Update_WithStaleExpectedVersion_Returns409()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "doc", tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        // 現在版は 1。期待版 5 は不一致 → 409
        var conflict = await client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "x", tags = new List<string>(), expectedVersion = 5 });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_SetsStatusPublished()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/documents",
            new { title = "to-publish", tags = new List<string>() });
        var doc = await create.Content.ReadFromJsonAsync<DocumentDto>();

        var publish = await client.PostAsync($"/documents/{doc!.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await publish.Content.ReadFromJsonAsync<DocumentDto>();
        published!.Status.Should().Be(DocumentStatus.Published);
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
