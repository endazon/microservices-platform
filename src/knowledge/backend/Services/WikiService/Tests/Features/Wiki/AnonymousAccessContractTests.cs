using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.Persistence;
using Wolverine;

namespace WikiService.Tests.Features.Wiki;

// UC-07 事前条件「**認証済み**」, FR-05, ADR-0032, IADR-0009, IADR-0044, IADR-0335（#1126）:
// Wiki 前段の 4 経路が、**未認証の要求に対して固定した応答を返す**こと。
//
// 決めた契約: **一覧・検索は 200 ＋ 空、個別（slug / documentId）は 404**（存在秘匿）。401 にはしない
// —— エッジは BFF（ADR-0032 / Token Handler）であり、ここは mesh 内の後段である。
//
// 🔴 **「fail-closed に見える」と「固定されている」は違う。** 従前は未認証でも `anonymous` を認可
// サービスへ投げていたため、**利用者条件を持たないポリシーが 1 件でも入れば匿名にも許可が下りた**。
// 本テストは認可サービスを**全許可（Granted=true・条件なし）で応答する構え**に置いたうえで、
//   ① 匿名は空／404 になること、② **認可サービスが 1 回も呼ばれていないこと**、
//   ③ 陽性対照として、同じ構えで**認証済みなら呼ばれて 200 が返る**こと
// を測る。②が無いと「たまたま拒否された」と区別できず、③が無いと「常に拒否する実装」が①を通す。
public class AnonymousAccessContractTests(AnonymousContractTestFactory factory)
    : IClassFixture<AnonymousContractTestFactory>
{
    private record Summary(Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath,
        string Status, DateTimeOffset SyncedAt);

    private async Task<(Guid DocumentId, string Slug)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        db.Pages.RemoveRange(db.Pages);
        await db.SaveChangesAsync();

        var page = WikiPage.CreateFromDocument(Guid.NewGuid(), "公開規程", "s3://b/pub.md",
            new() { ["confidentiality"] = "public" }, ["ops"]);
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        factory.WikiJs.Hits = [new WikiJsSearchHit(page.WikiPath, page.Title)];
        return (page.DocumentId, page.Slug);
    }

    // ① 未認証: 一覧は 200 ＋ 空。**認可サービスは呼ばれない。**
    [Fact]
    public async Task ListPages_ReturnsEmptyForAnonymous_WithoutAskingAuthorization()
    {
        await SeedAsync();
        factory.Authz.Calls = 0;

        var pages = await factory.CreateClient().GetFromJsonAsync<List<Summary>>(
            "/wiki/pages", TestContext.Current.CancellationToken);

        pages!.Should().BeEmpty("認可サービスが全許可を返す構えでも、匿名には何も見せない");
        factory.Authz.Calls.Should().Be(0, "未認証は認可サービスへ問い合わせる前に落ちる");
    }

    // ① 未認証: 検索も 200 ＋ 空。**認可サービスは呼ばれない。**
    [Fact]
    public async Task Search_ReturnsEmptyForAnonymous_WithoutAskingAuthorization()
    {
        await SeedAsync();
        factory.Authz.Calls = 0;

        var hits = await factory.CreateClient().GetFromJsonAsync<List<Summary>>(
            "/wiki/search?q=%E8%A6%8F%E7%A8%8B", TestContext.Current.CancellationToken);

        hits!.Should().BeEmpty();
        factory.Authz.Calls.Should().Be(0);
    }

    // ① 未認証: 個別（documentId / slug）はいずれも 404（存在秘匿・IADR-0009）。
    [Fact]
    public async Task GetPageByDocument_Returns404ForAnonymous()
    {
        var (documentId, _) = await SeedAsync();
        factory.Authz.Calls = 0;

        var response = await factory.CreateClient().GetAsync(
            $"/wiki/pages/by-doc/{documentId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Authz.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetPageBySlug_Returns404ForAnonymous()
    {
        var (_, slug) = await SeedAsync();
        factory.Authz.Calls = 0;

        var response = await factory.CreateClient().GetAsync(
            $"/wiki/pages/{slug}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Authz.Calls.Should().Be(0);
    }

    // 🔴 ③ 陽性対照（①と対）: **同じ構えで、認証済みなら 4 経路すべてが通る。**
    // これが無いと「常に空・常に 404 を返す実装」が上の 4 本を通してしまう。
    [Fact]
    public async Task AllFourRoutes_SucceedForAuthenticatedUser_AndAskAuthorization()
    {
        var (documentId, slug) = await SeedAsync();
        factory.Authz.Calls = 0;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestUserAuthHandler.UserHeader, "alice");

        var pages = await client.GetFromJsonAsync<List<Summary>>(
            "/wiki/pages", TestContext.Current.CancellationToken);
        var hits = await client.GetFromJsonAsync<List<Summary>>(
            "/wiki/search?q=%E8%A6%8F%E7%A8%8B", TestContext.Current.CancellationToken);
        var byDoc = await client.GetAsync(
            $"/wiki/pages/by-doc/{documentId}", TestContext.Current.CancellationToken);
        var bySlug = await client.GetAsync(
            $"/wiki/pages/{slug}", TestContext.Current.CancellationToken);

        pages!.Should().ContainSingle().Which.DocumentId.Should().Be(documentId);
        hits!.Should().ContainSingle().Which.DocumentId.Should().Be(documentId);
        byDoc.StatusCode.Should().Be(HttpStatusCode.OK);
        bySlug.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Authz.Calls.Should().BeGreaterThan(0, "認証済みなら認可サービスへ問い合わせる");
    }
}

// UC-07, #1126: 匿名契約テスト専用のホスト。**認可解決をスタブに差し替えない**
// （測る対象が `WikiAccessResolver` そのものだから）。代わりに認可サービスへの HTTP を
// 「全許可を返す記録ハンドラ」へ差し替える。
public class AnonymousContractTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"WikiAnonTest_{Guid.NewGuid()}";

    public RecordingAuthorizationHandler Authz { get; } = new();

    public SearchableWikiJsStub WikiJs { get; } = new();

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

            // 🔴 `IWikiAccessResolver` は**差し替えない**。実物の短絡を測るのが目的である。
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => Authz);

            services.RemoveAll<IWikiJsClient>();
            services.AddSingleton<IWikiJsClient>(WikiJs);
            services.RemoveAll<IWikiJsSearchClient>();
            services.AddSingleton<IWikiJsSearchClient>(WikiJs);

            // JWT/Keycloak に依存せず、ヘッダの有無で「未認証／認証済み」を切り替える。
            services.AddAuthentication(TestUserAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestUserAuthHandler>(
                    TestUserAuthHandler.SchemeName, _ => { });

            services.DisableAllExternalWolverineTransports();
        });
    }
}

// 認可サービスの応答を「**全許可**（Granted=true・条件なし）」に固定し、呼ばれた回数を数える。
// **これは最も甘い構えである** —— 未認証が素通りするなら、ここで必ず露見する。
public class RecordingAuthorizationHandler : HttpMessageHandler
{
    public int Calls { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        var body = """{"userId":"any","allowedFilters":[],"granted":true}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

// 検索ヒットを与えられる Wiki.js スタブ（本文は常に返す）。
public class SearchableWikiJsStub : IWikiJsClient, IWikiJsSearchClient
{
    public IReadOnlyList<WikiJsSearchHit> Hits { get; set; } = [];

    public Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default) => Task.CompletedTask;
    public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeletePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>($"<article data-path=\"{path}\">rendered</article>");

    public Task<IReadOnlyList<WikiJsSearchHit>> SearchAsync(string query, CancellationToken ct = default)
        => Task.FromResult(Hits);
}

// ヘッダ `X-Test-User` が在るときだけ認証済みにする。無いときは `NoResult`（＝未認証）。
// **「常に認証済み」にしてしまうと未認証の契約が測れない**ので、切り替え可能にしてある。
public class TestUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
