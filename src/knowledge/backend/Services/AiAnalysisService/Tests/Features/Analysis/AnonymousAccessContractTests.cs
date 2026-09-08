using AiAnalysisService.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiAnalysisService.Tests.Features.Analysis;

// FR-04, FR-05, FR-07, UC-01, UC-02, ADR-0004, ADR-0032, IADR-0009, IADR-0044,
// IADR-0335 決定 4（#1318 欠陥 A）:
// AiAnalysis 前段の 3 経路が、**未認証の要求に対して固定した応答を返す**こと。
//
// 決めた契約: **3 経路とも 200 ＋ 空回答**（`NoAccessAnswer`）。401 にはしない
// —— エッジは BFF（ADR-0032 / Token Handler。`/bff/analysis` 群が `RequireAuthorization()` を
// 持つ）であり、ここは mesh 内の後段である。**この 200 ＋ 空は従来と同じ値**であり、
// 本 PR が変えたのは「認可サービスを呼ばなくなる」ことだけである。
//
// 🔴 **「fail-closed に見える」と「固定されている」は違う。** 従前は未認証でも `anonymous` を
// 認可サービスへ投げていたため、**利用者条件を持たないポリシーが 1 件でも入れば匿名にも許可が
// 下りた**。本テストは認可サービスを**全許可（granted=true・条件なし）で応答する構え**に
// 置いたうえで、
//   ① 匿名は空回答になること、② **認可サービスが 1 回も呼ばれていないこと**、
//   ③ 陽性対照として、同じ構えで**認証済みなら呼ばれる**こと
// を測る。②が無いと「たまたま拒否された」と区別できず、③が無いと「常に拒否する実装」が①を通す。
//
// 🔴 **既存の器（`TestWebApplicationFactory`）では踏めない。** あれは `IRagOrchestrator` を丸ごと
// スタブへ差し替えるので、**端点を通る既存テストは ABAC を 1 度も踏んでいない**。本テスト専用の
// 器は resolver / orchestrator を差し替えず、認可サービスへの HTTP だけを記録ハンドラへ挿げ替える。
[Trait("TestKind", "Integration")]
public class AnonymousAccessContractTests(AnalysisAnonymousContractTestFactory factory)
    : IClassFixture<AnalysisAnonymousContractTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object AnalyzeBody = new
    {
        instruction = "2025 年の経費規程を比較して",
        taskType = "Compare",
    };

    // ① 未認証: /analysis/ask は 200 ＋ 空回答。**認可サービスは呼ばれない。**
    [Fact]
    public async Task Ask_ReturnsEmptyAnswerForAnonymous_WithoutAskingAuthorization()
    {
        factory.Authz.Calls = 0;

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/analysis/ask", new { question = "規程の更新手順は？" }, Ct);
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "401 にはしない（エッジは BFF）");
        answer!.Answer.Should().Be(NoAccessAnswer.Message);
        answer.Citations.Should().BeEmpty("認可サービスが全許可を返す構えでも、匿名には何も見せない");
        answer.Model.Should().Be(NoAccessAnswer.NoModel, "LLM を一度も呼んでいない");
        factory.Authz.Calls.Should().Be(0, "未認証は認可サービスへ問い合わせる前に落ちる");
    }

    // ① 未認証: /analysis/analyze も 200 ＋ 空回答。**認可サービスは呼ばれない。**
    [Fact]
    public async Task Analyze_ReturnsEmptyAnswerForAnonymous_WithoutAskingAuthorization()
    {
        factory.Authz.Calls = 0;

        var response = await factory.CreateClient().PostAsJsonAsync("/analysis/analyze", AnalyzeBody, Ct);
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        answer!.Answer.Should().Be(NoAccessAnswer.Message);
        answer.Citations.Should().BeEmpty();
        factory.Authz.Calls.Should().Be(0);
    }

    // ① 未認証: /analysis/ask/stream は 200 ＋ SSE 3 イベント。**認可サービスは呼ばれない。**
    // 🔴 SSE のまま返す（401 にしない）—— 中継・フロントから見て「拒否」と「見えるものが無い」を
    // 区別させない（存在秘匿・IADR-0009）。
    [Fact]
    public async Task AskStream_ReturnsNeutralSseForAnonymous_WithoutAskingAuthorization()
    {
        factory.Authz.Calls = 0;

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/analysis/ask/stream", new { question = "規程の更新手順は？" }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: citations").And.Contain("\"citations\":[]");
        body.Should().Contain("event: token").And.Contain(NoAccessAnswer.Message);
        body.Should().Contain("event: done");
        factory.Authz.Calls.Should().Be(0);
    }

    // 🔴 ③ 陽性対照（①と対）: **同じ構えで、認証済みなら 3 経路とも認可サービスへ問い合わせる。**
    // これが無いと「常に空回答を返す実装」が上の 3 本を通してしまう。
    //
    // 後段（RetrievalService / LlmGateway）は届かない構えなので本文は縮退し得る。
    // **ここで測るのは「ABAC を踏んだか」だけ**であり、回答本文の質ではない。
    [Theory]
    [InlineData("/analysis/ask")]
    [InlineData("/analysis/analyze")]
    [InlineData("/analysis/ask/stream")]
    public async Task AllThreeRoutes_AskAuthorizationForAuthenticatedUser(string path)
    {
        factory.Authz.Calls = 0;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AnalysisTestUserAuthHandler.UserHeader, "alice");

        object body = path == "/analysis/analyze"
            ? AnalyzeBody
            : new { question = "規程の更新手順は？" };
        var response = await client.PostAsJsonAsync(path, body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Authz.Calls.Should().BeGreaterThan(0, "認証済みなら認可サービスへ問い合わせる");
    }

    // 🔴 ③ 陽性対照（続き）: 認証済みの回答は**匿名の縮退と同じ文言にならない**。
    // 「常に `NoAccessAnswer` を返す実装」を落とす —— 全許可の構えなので、ABAC は通っている。
    [Fact]
    public async Task Ask_ForAuthenticatedUser_DoesNotReturnTheAnonymousDegradation()
    {
        factory.Authz.Calls = 0;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AnalysisTestUserAuthHandler.UserHeader, "alice");

        var answer = await client.PostAsJsonAsync("/analysis/ask", new { question = "規程は？" }, Ct)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<AiAnswerDto>(Ct), Ct).Unwrap();

        factory.Authz.Calls.Should().BeGreaterThan(0);
        answer!.Answer.Should().NotBe(NoAccessAnswer.Message,
            "全許可の構えで ABAC を通っているのだから、匿名と同じ縮退へは落ちない");
    }
}

// #1318: 匿名契約テスト専用のホスト。**`IRagOrchestrator` をスタブへ差し替えない**
// （測る対象が端点 → ABAC の経路そのものだから）。代わりに後段サービスへの HTTP を挿げ替える。
public class AnalysisAnonymousContractTestFactory : WebApplicationFactory<Program>
{
    public RecordingAuthorizationHandler Authz { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:AuthorizationService"] = "http://localhost:5005",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:LlmGateway"] = "http://localhost:5007",
            }));
        builder.ConfigureServices(services =>
        {
            // 🔴 `IRagOrchestrator` は**差し替えない**。実物の経路を測るのが目的である。
            services.RemoveAll<IServiceTokenProvider>();
            services.AddSingleton<IServiceTokenProvider>(new FixedServiceTokenProvider());
            services.AddHttpClient(AuthzScopeHttpClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Authz);

            // 後段は本テストの対象ではない。**到達不能を返す**（`RagOrchestrator` は検索失敗を
            // 空文脈へ、LLM 不達を縮退文言へ倒すので、認証済みの経路も 200 で終わる）。
            services.AddHttpClient("RetrievalService")
                .ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());
            services.AddHttpClient("LlmGateway")
                .ConfigurePrimaryHttpMessageHandler(() => new UnavailableHandler());

            // JWT/Keycloak に依存せず、ヘッダの有無で「未認証／認証済み」を切り替える。
            services.AddAuthentication(AnalysisTestUserAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AnalysisTestUserAuthHandler>(
                    AnalysisTestUserAuthHandler.SchemeName, _ => { });
        });
    }
}

// 認可サービスの応答を「**全許可**（granted=true・条件なし）」に固定し、呼ばれた回数を数える。
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

// 後段（検索・LLM）を不達にする器。**認可サービスには使わない。**
public class UnavailableHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
}

// ヘッダ `X-Test-User` が在るときだけ認証済みにする。無いときは `NoResult`（＝未認証）。
// **「常に認証済み」にしてしまうと未認証の契約が測れない**ので、切り替え可能にしてある。
public class AnalysisTestUserAuthHandler(
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

// #1333: 実 IdP を持たないテストで s2s トークンの**発行だけ**を固定する。
// 🔴 `ServiceTokenHandler` は本物が走る —— スコープ解決の要求に Bearer が載ることは変えない。
internal sealed class FixedServiceTokenProvider : IServiceTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult("test-service-token");
}
