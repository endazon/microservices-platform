using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace DocumentService.Api.Tests;

// FR-09, IADR-0044: 多層防御。文書の書き込みは BFF を迂回してもサービス単体で admin/operator を要求する。
// 読み取り（GET）は一般利用者の文書閲覧（SC-03）のため据え置き。非権限ロールの書き込みは 403、読み取りは可、
// 運用者の書き込みは許可されることを検証する（TestAuthHandler は常時認証のため 401 は対象外）。
public class DocumentAuthorizationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    [Fact]
    public async Task Create_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        var resp = await client.PostAsJsonAsync("/documents", new { title = "t" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        var resp = await client.DeleteAsync($"/documents/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_NonPrivilegedRole_IsAllowed()
    {
        // 読み取りはロール不要（一般利用者の閲覧）。非権限ロールでも 200。
        var client = ClientAs("viewer");
        (await client.GetAsync("/documents")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_OperatorRole_IsAllowed()
    {
        // 運用者は BFF write ゲート（IADR-0041）と同様に許可される。
        var client = ClientAs("platform-operator");
        var resp = await client.PostAsJsonAsync("/documents", new { title = "operator-doc" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
