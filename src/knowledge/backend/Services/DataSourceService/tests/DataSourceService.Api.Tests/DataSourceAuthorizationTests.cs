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

    // FR-09, IADR-0044: 残る書き込み/読み取りルート（個別取得・削除）も admin/operator を要求することを
    // 網羅検証する。認可はモデルバインド前に働くため対象存在に関わらず 403。将来いずれかのルートだけを
    // 誤って別グループへ移した際に回帰を検知できるようにする。
    [Theory]
    [InlineData("GET", "/datasources/{id}")]
    [InlineData("DELETE", "/datasources/{id}")]
    public async Task Route_NonPrivilegedRole_Returns403(string method, string template)
    {
        var client = ClientAs("viewer");
        var path = template.Replace("{id}", Guid.NewGuid().ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_OperatorRole_IsAllowed()
    {
        // 運用者は BFF ゲート（IADR-0039）と同様に許可される。
        var client = ClientAs("platform-operator");
        (await client.GetAsync("/datasources")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-01, SC-06（Q16 / #534）: **更新は管理者限定**である（計画 §SC-06「登録・更新・無効化は管理者限定」）。
    // グループ既定は admin + operator なので、新設した PUT / PATCH だけが AdminOnly を上書き要求する。
    // 運用者が閲覧できること（上のテスト）と対で固定し、上書きが外れたときに回帰を検知する。
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Update_OperatorRole_Returns403(string method)
    {
        var client = ClientAs("platform-operator");
        using var req = new HttpRequestMessage(new HttpMethod(method), $"/datasources/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new
            {
                name = "x",
                sourceType = "filesystem",
                connectionUri = "smb://x",
            }),
        };
        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Update_NonPrivilegedRole_Returns403(string method)
    {
        var client = ClientAs("viewer");
        using var req = new HttpRequestMessage(new HttpMethod(method), $"/datasources/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new
            {
                name = "x",
                sourceType = "filesystem",
                connectionUri = "smb://x",
            }),
        };
        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
