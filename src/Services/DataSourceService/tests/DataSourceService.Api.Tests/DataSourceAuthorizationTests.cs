using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace DataSourceService.Api.Tests;

// FR-09, IADR-0044: 多層防御。/datasources は BFF を迂回してもサービス単体で admin/operator を要求する。
// 非権限ロールは 403、運用者は許可されることを検証する（TestAuthHandler は常時認証のため 401 は対象外）。
public class DataSourceAuthorizationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    [Fact]
    public async Task List_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        (await client.GetAsync("/datasources")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "fs",
            sourceType = "filesystem",
            connectionUri = "smb://share/docs",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sync_NonPrivilegedRole_Returns403()
    {
        var client = ClientAs("viewer");
        var resp = await client.PostAsync($"/datasources/{Guid.NewGuid()}/sync", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_OperatorRole_IsAllowed()
    {
        // 運用者は BFF ゲート（IADR-0039）と同様に許可される。
        var client = ClientAs("platform-operator");
        (await client.GetAsync("/datasources")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
