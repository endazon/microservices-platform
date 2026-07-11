using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-06, UC-01/UC-03/UC-07, SC-03: /bff/documents が ABAC スコープ解決 → 文書取得を集約し、
// スコープ外・不在をいずれも 404 で秘匿すること（存在秘匿・IADR-0009/IADR-0038）、本文を
// ストレージ縮退込みで返すことを検証する。各テストはスタブ状態を変えるため直列（共有 fixture を汚さない）。
public class BffDocumentEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // 既定状態へ戻す（テスト間の状態リーク防止）。
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";

    [Fact]
    public async Task GetDetail_WhenAuthorizedAndAttributesInScope_ReturnsDocument()
    {
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentDto>();
        body!.Title.Should().Be("経費規程 2025");
        body.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetDetail_WhenScopeNotGranted_Returns404_DenyByDefault()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound); // 権限外は存在秘匿
    }

    // スコープは許可されていても、文書属性が許可フィルタに合致しなければ 404（存在秘匿）。
    [Fact]
    public async Task GetDetail_WhenAttributesOutOfScope_Returns404()
    {
        // 許可は confidentiality=secret のみ。文書は internal → 不一致。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["secret"])];
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 不在（DocumentService が 404）とスコープ外を区別しない（IADR-0009）。
    [Fact]
    public async Task GetDetail_WhenDocumentNotFound_Returns404()
    {
        _factory.DocumentStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // IADR-0038: 後段（DocumentService）不調はいずれも 404 へ縮退する（5xx も存在秘匿・区別しない）。
    [Fact]
    public async Task GetDetail_WhenDocumentServiceFails_Returns404()
    {
        _factory.DocumentStatusCode = HttpStatusCode.InternalServerError;
        var resp = await _factory.CreateClient().GetAsync(DetailPath);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetList_ReturnsOnlyInScopeDocuments()
    {
        // internal のみ許可 → secret 文書は列挙されない（権限内のみ）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];
        var resp = await _factory.CreateClient().GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentDto>>();
        body.Should().ContainSingle(d => d.Title == "経費規程 2025");
        body.Should().NotContain(d => d.Title == "取締役会議事録");
    }

    [Fact]
    public async Task GetList_WhenScopeNotGranted_ReturnsEmpty()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync("/bff/documents");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentDto>>();
        body!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetContent_WhenAuthorized_ReturnsMarkdownAndSourceUri()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/content");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentContentDto>();
        // ストレージ未配備（Null クライアント）のため本文はプレースホルダへ縮退し、タイトルを含む。
        body!.Markdown.Should().Contain("経費規程 2025");
        body.SourceUri.Should().Be("storage://bucket/expense.md");
    }

    [Fact]
    public async Task GetContent_WhenScopeNotGranted_Returns404()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/content");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVersions_WhenAuthorized_ReturnsHistory()
    {
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DocumentVersionDto>>();
        body!.Should().HaveCount(2);
        body[0].Version.Should().Be(3);
    }

    [Fact]
    public async Task GetVersions_WhenScopeNotGranted_Returns404()
    {
        _factory.SearchScopeGranted = false;
        var resp = await _factory.CreateClient().GetAsync($"{DetailPath}/versions");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
