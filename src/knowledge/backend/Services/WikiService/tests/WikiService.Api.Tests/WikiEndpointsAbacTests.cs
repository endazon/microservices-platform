using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Persistence;

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

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages", TestContext.Current.CancellationToken);

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

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages", TestContext.Current.CancellationToken);

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

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{restrictedDoc}", TestContext.Current.CancellationToken);

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

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{publicDoc}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<PageView>(TestContext.Current.CancellationToken);
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
        var response = await factory.CreateClient().GetAsync($"/wiki/pages/{restrictedSlug}", TestContext.Current.CancellationToken);

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
        var response = await factory.CreateClient().GetAsync($"/wiki/pages/{publicSlug}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<PageView>(TestContext.Current.CancellationToken);
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

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>("/wiki/pages", TestContext.Current.CancellationToken);

        pages!.Should().NotContain(p => p.DocumentId == publicDoc);
    }

    // Issue #88: アーカイブ済みページは権限があっても個別取得 404（存在秘匿の意味論を維持・IADR-0009）。
    [Fact]
    public async Task GetPageByDoc_Returns404_ForArchivedPage()
    {
        var (publicDoc, _) = await SeedAsync();
        await ArchiveAsync(publicDoc);
        factory.Scope = new AccessScopeResponse("u", [], Granted: true);

        var response = await factory.CreateClient().GetAsync($"/wiki/pages/by-doc/{publicDoc}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 段 3: 分岐（選言）の端点越しの適用 ----

    private async Task<(Guid MineDoc, Guid TheirsDoc, Guid OrgDoc)> SeedOwnerPagesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        db.Pages.RemoveRange(db.Pages);
        await db.SaveChangesAsync();

        var mine = WikiPage.CreateFromDocument(Guid.NewGuid(), "自分の個人資料", "s3://b/mine.md",
            new() { ["owner"] = "me" }, []);
        var theirs = WikiPage.CreateFromDocument(Guid.NewGuid(), "他人の個人資料", "s3://b/theirs.md",
            new() { ["owner"] = "someone-else" }, []);
        var org = WikiPage.CreateFromDocument(Guid.NewGuid(), "組織文書", "s3://b/org.md",
            new() { ["confidentiality"] = "internal" }, []);
        db.Pages.AddRange(mine, theirs, org);
        await db.SaveChangesAsync();
        return (mine.DocumentId, theirs.DocumentId, org.DocumentId);
    }

    // 認可サービスが段 2 以降に返す形: 所有者ベース（束縛済み）＋属性ベースの 2 分岐。
    private static AccessScopeResponse TwoBranchScope() =>
        new("me", [], Granted: true, Branches:
        [
            new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["me"])]),
            new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
        ]);

    // #989 回帰テスト 1・2（陽性対照）: 一覧に「自分の個人資料」と「組織文書」の**両方**が現れる。
    // 従来は連言に潰れて積集合しか見えず、owner 無し文書は全滅していた。
    // 否定形（3）と対: 「他人の個人資料」は現れない。
    [Fact]
    public async Task GetPages_WithBranches_ShowsOwnAndOrgButNotTheirs()
    {
        var (mineDoc, theirsDoc, orgDoc) = await SeedOwnerPagesAsync();
        factory.Scope = TwoBranchScope();

        var pages = await factory.CreateClient().GetFromJsonAsync<List<PageSummary>>(
            "/wiki/pages", TestContext.Current.CancellationToken);

        pages!.Select(p => p.DocumentId).Should().BeEquivalentTo([mineDoc, orgDoc],
            "所有者ベースの分岐と属性ベースの分岐の和が見え、他人の個人資料は見えない");
        pages!.Select(p => p.DocumentId).Should().NotContain(theirsDoc);
    }

    // #989 回帰テスト 3（否定形・端点越し）: 他人の個人資料は本文も 404（存在秘匿を維持）。
    [Fact]
    public async Task GetPageByDoc_WithBranches_Returns404_ForSomeoneElsesNote()
    {
        var (_, theirsDoc, _) = await SeedOwnerPagesAsync();
        factory.Scope = TwoBranchScope();

        var response = await factory.CreateClient().GetAsync(
            $"/wiki/pages/by-doc/{theirsDoc}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // #989 回帰テスト 4（陽性対照・3 と対）: 自分の個人資料は 200 で読める。
    // これが無いと「常に 404 を返す実装」が上の否定形を通す。
    [Fact]
    public async Task GetPageByDoc_WithBranches_Returns200_ForOwnNote()
    {
        var (mineDoc, _, _) = await SeedOwnerPagesAsync();
        factory.Scope = TwoBranchScope();

        var response = await factory.CreateClient().GetAsync(
            $"/wiki/pages/by-doc/{mineDoc}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
