using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.ExternalServices;
using WikiService.Infrastructure.Persistence;
using Wolverine;

namespace WikiService.Tests.Features.Wiki.SearchPages;

// UC-07 基本フロー 1「**検索する**」, FR-13, FR-05, FR-19, ADR-0011, IADR-0009, IADR-0331:
// Wiki 前段の検索経路に ABAC が適用され、**権限外の文書が検索結果に現れない**こと。
//
// 🔴 **否定形だけでは足りない。** 検索の検査は「出ない」ばかりになるため、**常に空を返す実装**でも
// 全部緑になる。**同じテスト群の中に陽性対照（見えるものは見える）を置く**
// （`WikiEndpointsAbacTests` が採っている作法と同じ）。
public class WikiSearchAbacTests(WikiSearchTestFactory factory) : IClassFixture<WikiSearchTestFactory>
{
    private record Hit(Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, DateTimeOffset SyncedAt);

    // 公開・機密・アーカイブ済みの 3 ページを台帳へ入れる。**Wiki.js 側のヒットは別に与える**
    // （委譲した検索が何を返してきても、前段が絞り直せることを測るため）。
    private async Task<(Guid Pub, Guid Restricted, Guid Archived)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        db.Pages.RemoveRange(db.Pages);
        await db.SaveChangesAsync();

        var pub = WikiPage.CreateFromDocument(Guid.NewGuid(), "公開規程", "s3://b/pub.md",
            new() { ["confidentiality"] = "public" }, ["ops"]);
        var restricted = WikiPage.CreateFromDocument(Guid.NewGuid(), "機密規程", "s3://b/sec.md",
            new() { ["confidentiality"] = "restricted" }, ["hr"]);
        var archived = WikiPage.CreateFromDocument(Guid.NewGuid(), "旧規程", "s3://b/old.md",
            new() { ["confidentiality"] = "public" }, []);
        archived.Archive();
        db.Pages.AddRange(pub, restricted, archived);
        await db.SaveChangesAsync();
        return (pub.DocumentId, restricted.DocumentId, archived.DocumentId);
    }

    private static AccessScopeResponse PublicOnly() =>
        new("u", [new AttributeFilter("confidentiality", ["public"])], Granted: true);

    private async Task<List<Hit>> SearchAsync(string query = "規程")
    {
        var hits = await factory.CreateClient().GetFromJsonAsync<List<Hit>>(
            $"/wiki/search?q={Uri.EscapeDataString(query)}", TestContext.Current.CancellationToken);
        hits.Should().NotBeNull();
        return hits!;
    }

    // FR-05 deny-by-default: 許可が下りていなければ空。**後段（Wiki.js）を叩かない。**
    [Fact]
    public async Task Search_ReturnsEmptyAndDoesNotCallWikiJs_WhenNotGranted()
    {
        var (pub, _, _) = await SeedAsync();
        factory.WikiJs.Hits = [new WikiJsSearchHit(WikiPage.PathFor(pub), "公開規程")];
        factory.WikiJs.Calls = 0;
        factory.Scope = new AccessScopeResponse("u", [], Granted: false);

        var hits = await SearchAsync();

        hits.Should().BeEmpty();
        factory.WikiJs.Calls.Should().Be(0, "許可が無いなら後段へ問い合わせる理由が無い");
    }

    // 🔴 陽性対照（上と対）: 許可が下りていれば後段が呼ばれ、権限内の文書が返る。
    // **これが無いと「常に空・常に呼ばない実装」が上のテストを通す。**
    [Fact]
    public async Task Search_CallsWikiJsAndReturnsPermittedPage_WhenGranted()
    {
        var (pub, _, _) = await SeedAsync();
        factory.WikiJs.Hits = [new WikiJsSearchHit(WikiPage.PathFor(pub), "公開規程")];
        factory.WikiJs.Calls = 0;
        factory.Scope = PublicOnly();

        var hits = await SearchAsync();

        factory.WikiJs.Calls.Should().Be(1);
        hits.Should().ContainSingle().Which.DocumentId.Should().Be(pub);
    }

    // 🔴 本命（UC-07 例外フロー）: Wiki.js が権限外の文書を返してきても、前段が落とす。
    // 同じ応答の中に権限内の文書を混ぜてあるので、**陽性対照を兼ねる**
    // （ADR-0011: Wiki.js 側の権限を属性ベース判定の代替にしない）。
    [Fact]
    public async Task Search_DropsRestrictedHit_ButKeepsPermittedHit()
    {
        var (pub, restricted, _) = await SeedAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit(WikiPage.PathFor(restricted), "機密規程"),
            new WikiJsSearchHit(WikiPage.PathFor(pub), "公開規程"),
        ];
        factory.Scope = PublicOnly();

        var hits = await SearchAsync();

        hits.Select(h => h.DocumentId).Should().BeEquivalentTo([pub],
            "Wiki.js が返しても、前段の ABAC を通らない文書は 1 件も現れない");
    }

    // Issue #88 の意味論を検索へも継承する: アーカイブ済みは権限があっても現れない。
    // 陽性対照として、同じ許可で公開ページは現れる。
    [Fact]
    public async Task Search_ExcludesArchivedPage_ButKeepsActiveOne()
    {
        var (pub, _, archived) = await SeedAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit(WikiPage.PathFor(archived), "旧規程"),
            new WikiJsSearchHit(WikiPage.PathFor(pub), "公開規程"),
        ];
        factory.Scope = PublicOnly();

        var hits = await SearchAsync();

        hits.Select(h => h.DocumentId).Should().BeEquivalentTo([pub]);
    }

    // IADR-0331: 台帳に足場を持たないページ（Wiki.js 上で人手で作られた等）は ABAC 判定できないので落ちる。
    // 陽性対照として、同じ応答に混ぜた台帳のページは現れる。
    [Fact]
    public async Task Search_DropsHitsWithoutLedgerEntry()
    {
        var (pub, _, _) = await SeedAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit("home", "ホーム"),
            new WikiJsSearchHit($"doc/{Guid.NewGuid()}", "台帳に無い文書"),
            new WikiJsSearchHit(WikiPage.PathFor(pub), "公開規程"),
        ];
        factory.Scope = PublicOnly();

        var hits = await SearchAsync();

        hits.Select(h => h.DocumentId).Should().BeEquivalentTo([pub]);
    }

    // ---- FR-19, ADR-0036, IADR-0253: 分岐（選言）が検索経路でも効く ----

    private async Task<(Guid Mine, Guid Theirs, Guid Org)> SeedOwnerPagesAsync()
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

    // 分岐間 OR・分岐内 AND が検索でも効く。**他人の個人資料は現れず**、自分のものと組織文書は現れる
    // （否定形と陽性対照が同じ 1 本に入っている）。
    [Fact]
    public async Task Search_WithBranches_ShowsOwnAndOrgButNotTheirs()
    {
        var (mine, theirs, org) = await SeedOwnerPagesAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit(WikiPage.PathFor(mine), "自分の個人資料"),
            new WikiJsSearchHit(WikiPage.PathFor(theirs), "他人の個人資料"),
            new WikiJsSearchHit(WikiPage.PathFor(org), "組織文書"),
        ];
        factory.Scope = new AccessScopeResponse("me", [], Granted: true, Branches:
        [
            new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["me"])]),
            new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
        ]);

        var hits = await SearchAsync("資料");

        hits.Select(h => h.DocumentId).Should().BeEquivalentTo([mine, org]);
        hits.Select(h => h.DocumentId).Should().NotContain(theirs);
    }

    // IADR-0331: **Wiki.js の関連度順を保つ**（台帳の並びで上書きしない）。
    [Fact]
    public async Task Search_PreservesWikiJsRelevanceOrder()
    {
        var (mine, _, org) = await SeedOwnerPagesAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit(WikiPage.PathFor(org), "組織文書"),
            new WikiJsSearchHit(WikiPage.PathFor(mine), "自分の個人資料"),
        ];
        factory.Scope = new AccessScopeResponse("me", [], Granted: true);

        var hits = await SearchAsync("資料");

        hits.Select(h => h.DocumentId).Should().ContainInOrder(org, mine);
    }

    // 過大要求の抑止: limit は 1..50 へクランプされる（0 以下は既定）。
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    [InlineData(999, 2)]
    public async Task Search_ClampsLimit(int requested, int expected)
    {
        var (mine, _, org) = await SeedOwnerPagesAsync();
        factory.WikiJs.Hits =
        [
            new WikiJsSearchHit(WikiPage.PathFor(org), "組織文書"),
            new WikiJsSearchHit(WikiPage.PathFor(mine), "自分の個人資料"),
        ];
        factory.Scope = new AccessScopeResponse("me", [], Granted: true);

        var hits = await factory.CreateClient().GetFromJsonAsync<List<Hit>>(
            $"/wiki/search?q=%E8%B3%87%E6%96%99&limit={requested}", TestContext.Current.CancellationToken);

        hits!.Should().HaveCount(expected);
    }

    // 空クエリは後段を叩かずに空を返す。
    [Fact]
    public async Task Search_ReturnsEmptyWithoutCallingWikiJs_ForBlankQuery()
    {
        await SeedAsync();
        factory.WikiJs.Calls = 0;
        factory.Scope = PublicOnly();

        var hits = await factory.CreateClient().GetFromJsonAsync<List<Hit>>(
            "/wiki/search?q=%20", TestContext.Current.CancellationToken);

        hits!.Should().BeEmpty();
        factory.WikiJs.Calls.Should().Be(0);
    }

    // 🔴 IADR-0331 / IADR-0256: **後段の故障を 200 ＋ 空で隠さない。**
    // 存在秘匿が区別させないのは「権限が無い」と「該当が無い」であり、「壊れている」は別の軸である。
    [Fact]
    public async Task Search_Returns502_WhenWikiJsIsUnavailable()
    {
        await SeedAsync();
        factory.Scope = PublicOnly();
        factory.WikiJs.Failure = new WikiJsSyncException("Wiki.js GraphQL error: boom");

        try
        {
            var response = await factory.CreateClient().GetAsync(
                "/wiki/search?q=%E8%A6%8F%E7%A8%8B", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            factory.WikiJs.Failure = null;
        }
    }
}

// UC-07, #1126: 検索テスト専用のホスト。既存の `TestWebApplicationFactory` は Wiki.js スタブが
// **常に空**を返す前提で書かれており、検索のヒットを制御できない。**既存ファイルへ手を入れずに**
// 制御可能なスタブを持つホストをここへ置く（#1063 の `Tests/` 移送と衝突させない）。
public class WikiSearchTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"WikiSearchTest_{Guid.NewGuid()}";

    public AccessScopeResponse Scope { get; set; } = new("test-user", [], true);

    public ControllableWikiJsClient WikiJs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            }));
        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WikiDbContext>)
                         || (d.ServiceType.IsGenericType
                             && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                             && d.ServiceType.GenericTypeArguments.Length == 1
                             && d.ServiceType.GenericTypeArguments[0] == typeof(WikiDbContext)))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);
            services.AddDbContext<WikiDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

            services.RemoveAll<IWikiAccessResolver>();
            services.AddSingleton<IWikiAccessResolver>(new StubScopeResolver(this));

            services.RemoveAll<IWikiJsSearchClient>();
            services.AddSingleton<IWikiJsSearchClient>(WikiJs);

            // 🔴 これが無いとテストが実ブローカへの接続で長くハングする（既存ファクトリと同じ理由）。
            services.DisableAllExternalWolverineTransports();
        });
    }
}

// 検索のヒット・呼び出し回数・故障を試験から制御できる Wiki.js 検索スタブ。
// **同期の口（`IWikiJsClient`）には触らない** —— 検索の口が分かれているので、同期側のスタブを
// 抱え込まずに済む（[[IADR-0331]] 決定 3）。
public class ControllableWikiJsClient : IWikiJsSearchClient
{
    public IReadOnlyList<WikiJsSearchHit> Hits { get; set; } = [];
    public int Calls { get; set; }
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<WikiJsSearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        return Task.FromResult(Hits);
    }
}

// 許可スコープをファクトリから差し替えるスタブ（既存ファクトリと同じ方針）。
file class StubScopeResolver(WikiSearchTestFactory factory) : IWikiAccessResolver
{
    public Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
        => Task.FromResult(factory.Scope);
}
