using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, IADR-0041: /bff/documents の書き込み（作成・更新・メタ更新・公開・アーカイブ・削除）が
// 管理者・運用者に限定されること、スコープ外文書の変更は 404 秘匿されること、検証 400・楽観ロック競合 409 を
// 透過することを検証する。各テストはスタブ状態を変えるため直列（共有 fixture を汚さない）。
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
