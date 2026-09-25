using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs;

// NFR-09, FR-12, UC-06, SC-07, ADR-0109 決定 3, ADR-0084 決定 1, IADR-0465 (#1520):
// **ConversionService は BFF が中継した利用者の資格情報を自ら検証する。** 従前は認証を持たず、門は BFF の
// 1 枚だけだった（IADR-0403 決定 4・IADR-0458 決定 1）。本クラスは本番の `Program.cs` を
// `WebApplicationFactory` で起こし、**本物の JwtBearer**（検証鍵だけテスト用）で `/jobs` の 5 口の門を測る。
//
// ADR-0084 決定 1 は NFR-09 を**端点単位**で判定する —— 5 口を 1 つずつ並べるのはそのためである
// （群に掛けた門を 1 口で確かめても、別の群へ移された口は見えない）。
//
// 🔴 **ソケットを開かない**（TestServer はメモリ内。`TestUserTokens` の注記を見よ）。
[Trait("TestKind", "Integration")]
public class ConversionJobAuthorizationTests
{
    private const string AdminRole = "platform-admin";
    private const string OperatorRole = "platform-operator";

    // 5 口の一覧（method, path の雛形）。`{id}` は実行時に既知のジョブ id へ置き換える。
    public static TheoryData<string, string> AllRoutes => new()
    {
        { "GET", "/jobs" },
        { "GET", "/jobs/{id}" },
        { "POST", "/jobs/{id}/retry" },
        { "GET", "/jobs/{id}/figures" },
        { "POST", "/jobs/{id}/figures/fig-1/correction" },
    };

    public static TheoryData<string, string> AdminOnlyRoutes => new()
    {
        { "POST", "/jobs/{id}/retry" },
        { "GET", "/jobs/{id}/figures" },
        { "POST", "/jobs/{id}/figures/fig-1/correction" },
    };

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // 失敗ジョブを 1 件置く（再変換が 202 を返せる状態）。
    private static async Task<Guid> SeedFailedJobAsync(Factory factory)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
        await store.StartAsync(Raw(id));
        await store.FailAsync(id, "失敗");
        return id;
    }

    private static HttpRequestMessage Request(string method, string template, Guid id, string? bearer)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), template.Replace("{id}", id.ToString()));
        if (template.EndsWith("/correction", StringComparison.Ordinal))
            req.Content = JsonContent.Create(new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"));
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static async Task<HttpStatusCode> SendAsync(Factory factory, string method, string template,
        Guid id, string? bearer)
    {
        using var client = factory.CreateClient();
        using var req = Request(method, template, id, bearer);
        using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        return resp.StatusCode;
    }

    // NFR-09, SC-07, ADR-0109 決定 3: 資格情報なしは 5 口すべてで 401。
    // 従前（認証なし）はここが 200 / 202 / 404 を返していた —— メッシュ内から直に叩けば BFF の門を迂回できた。
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task EveryRoute_WithoutCredential_Returns401(string method, string template)
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);

        (await SendAsync(factory, method, template, id, bearer: null))
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // NFR-09, ADR-0109 決定 3: **検証しているのは「Bearer が付いていること」ではなく資格情報そのもの**である。
    // 署名が違う（偽造）・発行元が違う・期限切れのトークンは、管理者ロールを名乗っていても 401。
    [Theory]
    [InlineData("forged")]
    [InlineData("foreign-issuer")]
    [InlineData("expired")]
    public async Task InvalidToken_EvenClaimingAdmin_Returns401(string kind)
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = kind switch
        {
            "forged" => TestUserTokens.Issue("mallory", [AdminRole], forged: true),
            "foreign-issuer" => TestUserTokens.Issue("mallory", [AdminRole],
                issuer: "https://other-idp/realms/platform"),
            _ => TestUserTokens.Issue("alice", [AdminRole], expired: true),
        };

        (await SendAsync(factory, "GET", "/jobs", id, token)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(factory, "POST", "/jobs/{id}/retry", id, token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    // NFR-09, SC-07, IADR-0042 決定 3（閲覧は管理者・運用者）: 門のロールを持たない利用者は 5 口すべてで 403。
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task EveryRoute_UserWithoutGateRole_Returns403(string method, string template)
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = TestUserTokens.Issue("bob", ["default-roles-platform"]);

        (await SendAsync(factory, method, template, id, token)).Should().Be(HttpStatusCode.Forbidden);
    }

    // NFR-09, ADR-0029, IADR-0379 決定 4, IADR-0465 決定 2: **`platform-service` だけのサービス間トークンは通さない。**
    // `/jobs` をサービスとして呼ぶ呼び出し元は無い。通すと「利用者が操作した」と区別できない呼び出しが開く。
    // （門はロールで判定するので、門のロールを持つサービスアカウントは通る。BFF・他の後段と同じ性質。）
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task EveryRoute_ServiceAccountToken_Returns403(string method, string template)
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = TestUserTokens.Issue("service-account-document-service", ["platform-service"]);

        (await SendAsync(factory, method, template, id, token)).Should().Be(HttpStatusCode.Forbidden);
    }

    // NFR-09, SC-07, IADR-0128 決定 2: 中継された運用者のトークンで照会（一覧・個別）は 200。
    [Fact]
    public async Task Queries_WithRelayedOperatorToken_Return200()
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = TestUserTokens.Issue("olivia", [OperatorRole]);

        (await SendAsync(factory, "GET", "/jobs", id, token)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(factory, "GET", "/jobs/{id}", id, token)).Should().Be(HttpStatusCode.OK);
    }

    // NFR-09, SC-07, IADR-0128 決定 1, IADR-0154 決定 6: 再変換と人手補正（図の一覧を含む）は管理者限定。
    // **運用者は照会できるが、この 3 口には入れない**（BFF と同じ境界。片側だけ緩いと直呼びで緩い側が効く）。
    [Theory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task AdminOnlyRoutes_WithRelayedOperatorToken_Return403(string method, string template)
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = TestUserTokens.Issue("olivia", [OperatorRole]);

        (await SendAsync(factory, method, template, id, token)).Should().Be(HttpStatusCode.Forbidden);
    }

    // NFR-09, SC-07, ADR-0109 決定 3: 中継された管理者のトークンは 5 口すべてで門を通り、各口の本来の応答になる。
    // 人手補正は既知のジョブに存在しない図を指すので 404（門を通った先の判定）である。
    [Fact]
    public async Task EveryRoute_WithRelayedAdminToken_PassesTheGate()
    {
        using var factory = new Factory();
        var id = await SeedFailedJobAsync(factory);
        var token = TestUserTokens.Issue("alice", [AdminRole]);

        (await SendAsync(factory, "GET", "/jobs", id, token)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(factory, "GET", "/jobs/{id}", id, token)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(factory, "GET", "/jobs/{id}/figures", id, token)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(factory, "POST", "/jobs/{id}/figures/fig-1/correction", id, token))
            .Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(factory, "POST", "/jobs/{id}/retry", id, token)).Should().Be(HttpStatusCode.Accepted);
    }

    // NFR-09, FR-15, IADR-0029, IADR-0465 決定 3: ヘルスチェックと自己申告は他サービスと同じく門を持たない
    // （プローブと構成情報の収集は利用者の資格情報を持たない）。
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/internal/introspection")]
    public async Task ProbeAndIntrospection_WithoutCredential_Return200(string path)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // NFR-09, IADR-0465 決定 3: readiness も門を持たない。テストでは DB が InMemory で依存先の検査は
    // 赤くなり得るので、状態コードは「認証・認可で弾かれていない」ことだけを見る。
    [Fact]
    public async Task Readiness_WithoutCredential_IsNotGated()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var status = (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode;

        status.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
    }

    // NFR-09, IADR-0465 決定 1: `UsePlatformMiddleware` は認証・認可だけでなく相関 ID も張る（他サービスと同じ）。
    // 受け取った相関 ID は応答へ返り、**門で弾かれた 401 にも付く**（ミドルウェアが認証より前に居る）。
    // `UsePlatformMiddleware` を `UseAuthentication` / `UseAuthorization` だけへ差し替えると、ここが落ちる。
    [Fact]
    public async Task PlatformMiddleware_EchoesCorrelationId_EvenOnRejectedRequest()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        probe.Headers.Add("X-Correlation-ID", "cv-corr-1");
        using var probeResp = await client.SendAsync(probe, TestContext.Current.CancellationToken);
        probeResp.Headers.GetValues("X-Correlation-ID").Should().ContainSingle().Which.Should().Be("cv-corr-1");

        using var rejected = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        rejected.Headers.Add("X-Correlation-ID", "cv-corr-2");
        using var rejectedResp = await client.SendAsync(rejected, TestContext.Current.CancellationToken);
        rejectedResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        rejectedResp.Headers.GetValues("X-Correlation-ID").Should().ContainSingle().Which.Should().Be("cv-corr-2");
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Otlp:Endpoint"] = "http://localhost:4317"
                }));
            builder.ConfigureServices(services =>
            {
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>(_dbName);
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
                // Program.cs は Wolverine ホストも起こす。外部トランスポートを落とさないと実 RabbitMQ への
                // 接続再試行で黙って遅くなる（ConversionJobEndpointTests と同じ理由）。
                services.DisableAllExternalWolverineTransports();
                // 再変換の発行を実ブローカへ出さない。
                services.RemoveAll<IMessageBus>();
                services.AddSingleton<RecordingMessageBus>();
                services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RecordingMessageBus>());

                // 門は本物の JwtBearer で判定する。差し替えるのは metadata の取得先と検証鍵だけ。
                TestUserTokens.UseStaticJwtBearer(services);
            });
        }
    }
}
