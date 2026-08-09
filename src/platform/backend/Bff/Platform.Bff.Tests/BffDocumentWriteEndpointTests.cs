using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, IADR-0041: /bff/documents の書き込み（作成・更新・公開・アーカイブ・削除）が
// **管理者限定**であること、スコープ外文書の変更は 404 秘匿されること、検証 400・楽観ロック競合 409 を
// 透過することを検証する。各テストはスタブ状態を変えるため直列（共有 fixture を汚さない）。
//
// **［#629］「管理者・運用者」から「管理者限定」へ狭めた。** 計画 §SC-05（裁定 Q19）が
// **閲覧は管理者・運用者／破壊的操作は管理者限定**と定めており、実装だけが運用者にも開いていた。
// **後段（`DocumentEndpoints`）にも同じ制限がある**（[[IADR-0044]] の多層防御）ので、
// **両側を別々のテストが押さえる** —— 片側だけ直すと BFF 迂回で通るか、画面だけ 403 になる。
public class BffDocumentWriteEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentWriteEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentWriteStatusCode = HttpStatusCode.OK;
    }

    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents",
            new { title = "新規文書", attributes = new { confidentiality = "internal" }, tags = new[] { "hr" } });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_AsNonPrivilegedRole_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        var resp = await client.PostAsJsonAsync("/bff/documents", new { title = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── #629: 運用者は 5 口すべてで 403（受け入れ基準 2）───────────────────────
    //
    // **`Create_AsNonPrivilegedRole_IsForbidden` では代わりにならない** ——
    // あちらは `viewer` でグループ既定を検査しており、**グループ既定は据え置いた**ので
    // `AdminOnly` を 1 つも積まなくても緑になる。**運用者で引くことがこの作業の検査である。**
    [Theory]
    [InlineData("POST", "/bff/documents")]
    [InlineData("PUT", "/bff/documents/{id}")]
    [InlineData("POST", "/bff/documents/{id}/publish")]
    [InlineData("POST", "/bff/documents/{id}/archive")]
    [InlineData("DELETE", "/bff/documents/{id}")]
    public async Task Write_AsOperator_IsForbidden(string method, string template)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var path = template.Replace("{id}", BffTestFactory.StubDocumentId.ToString());
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { title = "x" })
        };
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 受け入れ基準 4（対）: 運用者の**閲覧は従来どおり**塞がれていない（Q19）。
    // これが無いと、読み取りグループまで狭めてしまっても上のテストは緑のまま通る。
    [Theory]
    [InlineData("/bff/documents")]
    [InlineData("/bff/documents/{id}")]
    [InlineData("/bff/documents/{id}/versions")]
    [InlineData("/bff/documents/{id}/content")]
    public async Task Read_AsOperator_IsStillAllowed(string template)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await client.GetAsync(template.Replace("{id}", BffTestFactory.StubDocumentId.ToString()));

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.PostAsJsonAsync("/bff/documents", new { title = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WhenScopeNotGranted_IsForbidden_DenyByDefault()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents", new { title = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_WhenTitleMissing_Passes400Through()
    {
        _factory.DocumentWriteStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/documents", new { title = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_AsAdminInScope_Returns200()
    {
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath,
            new { title = "改訂", attributes = new { confidentiality = "internal" }, tags = Array.Empty<string>(), expectedVersion = 3 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WhenOutOfScope_Returns404()
    {
        // 許可は secret のみ。対象文書は internal → スコープ外 → 変更不可（存在秘匿）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath, new { title = "改訂" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_WhenVersionConflict_Passes409Through()
    {
        _factory.DocumentWriteStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient().PutAsJsonAsync(DetailPath,
            new { title = "改訂", expectedVersion = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Publish_AsAdminInScope_Returns200()
    {
        var resp = await _factory.CreateClient().PostAsync($"{DetailPath}/publish", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_AsAdminInScope_Returns204()
    {
        var resp = await _factory.CreateClient().DeleteAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WhenOutOfScope_Returns404()
    {
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().DeleteAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
