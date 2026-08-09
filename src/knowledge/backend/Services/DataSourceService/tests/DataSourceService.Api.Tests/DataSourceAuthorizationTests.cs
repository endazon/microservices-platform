using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // FR-01, SC-06（#628）: **登録は管理者限定**である（計画 §SC-06・裁定 Q19「破壊的操作は管理者限定」）。
    // 従前はグループ既定（admin + operator）のままで、計画より広かった。
    [Fact]
    public async Task Create_OperatorRole_Returns403()
    {
        var client = ClientAs("platform-operator");
        var resp = await client.PostAsJsonAsync("/datasources", new
        {
            name = "fs",
            sourceType = "filesystem",
            connectionUri = "smb://share/docs",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-01, SC-06（#628）: **無効化（論理削除）も管理者限定**である。
    [Fact]
    public async Task Delete_OperatorRole_Returns403()
    {
        var client = ClientAs("platform-operator");
        (await client.DeleteAsync($"/datasources/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-01, UC-04, SC-06（#628 / planning#299・2026-08-09）: **手動同期は破壊的操作に含めない。**
    // 運用者に開いたままであることを固定する——登録・無効化を狭めた勢いで一緒に絞ると、
    // 「運用者が異常に気づいたその場で再同期して一次対応する」という計画の裁定を壊す。
    // 対象が存在しないので 404 だが、**403 ではない**ことが認可を通過した証拠になる。
    [Fact]
    public async Task Sync_OperatorRole_IsAllowed()
    {
        var client = ClientAs("platform-operator");
        var resp = await client.PostAsync($"/datasources/{Guid.NewGuid()}/sync", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-01, SC-06（#628）: 個別取得も運用者に開いたままである（**閲覧を狭めない**）。
    [Fact]
    public async Task GetById_OperatorRole_IsAllowed()
    {
        var client = ClientAs("platform-operator");
        (await client.GetAsync($"/datasources/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-01, SC-06（#628）: 管理者は登録・無効化を従来どおり実行できる（狭めすぎていないこと）。
    [Fact]
    public async Task CreateAndDelete_AdminRole_IsAllowed()
    {
        var client = ClientAs("platform-admin");
        var created = await client.PostAsJsonAsync("/datasources", new
        {
            name = "admin-only-fs",
            sourceType = "filesystem",
            connectionUri = "smb://share/admin",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await created.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var id = body!["id"].GetGuid();
        (await client.DeleteAsync($"/datasources/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
