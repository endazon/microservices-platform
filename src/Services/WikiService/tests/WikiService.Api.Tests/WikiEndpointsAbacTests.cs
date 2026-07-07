using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using WikiService.Api.Domain;
using WikiService.Api.Infrastructure;

namespace WikiService.Api.Tests;

// FR-13, FR-05, UC-07: Wiki 閲覧 API に ABAC が適用され、権限外文書が一覧・本文のいずれにも現れないこと。
public class WikiEndpointsAbacTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record PageSummary(Guid Id, Guid DocumentId, string Title, string Slug, string Status, DateTimeOffset SyncedAt);

    private async Task<(Guid PublicDoc, Guid RestrictedDoc)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        db.Pages.RemoveRange(db.Pages);
        await db.SaveChangesAsync();

        var pub = WikiPage.CreateFromDocument(Guid.NewGuid(), "公開規程", "s3://b/pub.md",
            new() { ["confidentiality"] = "public" }, ["ops"]);
        var restricted = WikiPage.CreateFromDocument(Guid.NewGuid(), "機密規程", "s3://b/sec.md",
            new() { ["confidentiality"] = "restricted" }, ["hr"]);
        db.Pages.AddRange(pub, restricted);
        await db.SaveChangesAsync();
        return (pub.DocumentId, restricted.DocumentId);
    }

    // deny-by-default: Granted=false なら一覧は空。
    [Fact]
    public async Task GetPages_ReturnsEmpty_WhenNotGranted()
    {
        await SeedAsync();
        factory.Scope = new AccessScopeResponse("u", [], Granted: false);

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages");

        pages.Should().NotBeNull();
        pages!.Should().BeEmpty();
    }

    // 一覧は権限内の属性を持つページのみを返す（権限外は現れない）。
    [Fact]
    public async Task GetPages_ReturnsOnlyPermittedPages()
    {
        await SeedAsync();
        factory.Scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public", "internal"])], Granted: true);

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages");

        pages.Should().ContainSingle();
        pages![0].Title.Should().Be("公開規程");
    }

    // 個別（by-doc）: 権限外文書は本文も 404（存在を秘匿）。
    [Fact]
    public async Task GetPageByDoc_Returns404_ForRestrictedDocument()
    {
        var (_, restrictedDoc) = await SeedAsync();
        factory.Scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public"])], Granted: true);

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{restrictedDoc}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record PageView(Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath,
        string Status, DateTimeOffset SyncedAt, string Content);

    // 個別（by-doc）: 権限内文書は 200 で、本文を Wiki.js からプロキシして返す（IADR-0020 認可ゲートウェイ）。
    [Fact]
    public async Task GetPageByDoc_Returns200_ProxyingWikiJsContent_ForPermittedDocument()
    {
        var (publicDoc, _) = await SeedAsync();
        factory.Scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public"])], Granted: true);

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{publicDoc}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<PageView>();
        view.Should().NotBeNull();
        // 本文は自前 DB ではなく Wiki.js（スタブ）から取得され、DocumentId 由来の安定パスを指す。
        view!.WikiPath.Should().Be($"doc/{publicDoc}");
        view.Content.Should().Contain($"doc/{publicDoc}");
    }

    // 個別（slug）: 権限外文書は 404。
    [Fact]
    public async Task GetPageBySlug_Returns404_ForRestrictedDocument()
    {
        await SeedAsync();
        factory.Scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public"])], Granted: true);

        // "機密規程" の slug（ToSlug 適用後）を直接取得しても 404。
        var restrictedSlug = ResolveSlug("機密規程");
        var response = await factory.CreateClient().GetAsync($"/wiki/pages/{restrictedSlug}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 個別（slug）: 権限内文書は 200 で、本文を Wiki.js からプロキシして返す（by-doc と対称）。
    [Fact]
    public async Task GetPageBySlug_Returns200_ProxyingWikiJsContent_ForPermittedDocument()
    {
        var (publicDoc, _) = await SeedAsync();
        factory.Scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public"])], Granted: true);

        var publicSlug = ResolveSlug("公開規程");
        var response = await factory.CreateClient().GetAsync($"/wiki/pages/{publicSlug}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<PageView>();
        view.Should().NotBeNull();
        // 本文は自前 DB ではなく Wiki.js（スタブ）から取得され、DocumentId 由来の安定パスを指す。
        view!.WikiPath.Should().Be($"doc/{publicDoc}");
        view.Content.Should().Contain($"doc/{publicDoc}");
    }

    // Issue #88: アーカイブ済みページは権限があっても一覧に現れない（非公開化の伝播）。
    [Fact]
    public async Task GetPages_ExcludesArchivedPages()
    {
        var (publicDoc, _) = await SeedAsync();
        await ArchiveAsync(publicDoc);
        factory.Scope = new AccessScopeResponse("u", [], Granted: true);

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages");

        pages!.Should().NotContain(p => p.DocumentId == publicDoc);
    }

    // Issue #88: アーカイブ済みページは権限があっても個別取得 404（存在秘匿の意味論を維持・IADR-0009）。
    [Fact]
    public async Task GetPageByDoc_Returns404_ForArchivedPage()
    {
        var (publicDoc, _) = await SeedAsync();
        await ArchiveAsync(publicDoc);
        factory.Scope = new AccessScopeResponse("u", [], Granted: true);

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{publicDoc}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task ArchiveAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        var page = db.Pages.First(p => p.DocumentId == documentId);
        page.Archive();
        await db.SaveChangesAsync();
    }

    private string ResolveSlug(string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        return db.Pages.First(p => p.Title == title).Slug;
    }
}
