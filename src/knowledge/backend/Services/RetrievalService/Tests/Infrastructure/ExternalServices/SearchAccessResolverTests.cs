using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, NFR-09, UC-01, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034 決定 1,
// ADR-0075, [[IADR-0044]], [[IADR-0272]] 決定 4, [[IADR-0335]] 決定 4, [[IADR-0379]] 決定 5,
// [[IADR-0401]] 決定 1, [[IADR-0416]] (#1339):
// **受け口が自分で引く許可スコープ**の解決器そのものへ掛ける固定。
//
// 🔴 **器（`TestWebApplicationFactory`）はこの解決器をスタブする。** したがって
// **端点の試験ではここの短絡も縮退も 1 行も通らない** —— 変異試験で実際に生存した
// （未認証の短絡を外しても端点側は全緑だった）。**解決器には解決器の試験が要る。**
[Trait("TestKind", "Unit")]
public class SearchAccessResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpContext Authenticated() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("clearance", "internal"),
            new Claim("department", "engineering"),
        ], "Test")),
    };

    // 認証されていない主体（`ClaimsIdentity` に認証方式を与えない ＝ IsAuthenticated == false）。
    private static HttpContext Anonymous() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity()),
    };

    private static AccessScopeResponse Granted() => new(
        "alice",
        [new AttributeFilter("confidentiality", ["internal", "public"])],
        true,
        [new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal", "public"])])]);

    private static string GrantedJson =>
        System.Text.Json.JsonSerializer.Serialize(
            Granted(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    // 🔴 T-01（本体）: **未認証は認可サービスを 1 度も呼ばない。**
    //
    // 呼ぶと、**利用者条件を持たないポリシーが 1 件でも active なら匿名にも許可が下りる**
    // （`AbacEvaluator` は条件が空なら全利用者にマッチする。FR-05 の意図）——
    // #1126 が Wiki で実測した欠陥と同型である。**呼び出し回数 0 でしか表明できない。**
    [Fact]
    public async Task An_unauthenticated_request_never_reaches_the_authorization_service()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        var scope = await new SearchAccessResolver(new NeverHttpClientFactory(), fake.Wrap())
            .ResolveAsync(Anonymous(), Ct);

        fake.CallCount.Should().Be(0, "未認証は後段へ問い合わせない（多層防御）");
        scope.Granted.Should().BeFalse();
        scope.UserId.Should().Be("anonymous");
    }

    // 陽性対照（T-01 と対）: 認証済みなら実際に呼ぶ。
    // これが無いと「偽物が壊れていて常に 0 回」と区別できない。
    [Fact]
    public async Task An_authenticated_request_does_reach_the_authorization_service()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        var scope = await new SearchAccessResolver(new NeverHttpClientFactory(), fake.Wrap())
            .ResolveAsync(Authenticated(), Ct);

        fake.CallCount.Should().Be(1, "★ 陽性対照");
        scope.Granted.Should().BeTrue();
    }

    // 🔴 T-02: **action は既定へ頼らず明示する**（[[IADR-0272]] 決定 4）。
    // 検索・属性値照会は閲覧経路なので read である。
    [Fact]
    public async Task It_asks_for_the_read_action_explicitly()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        await new SearchAccessResolver(new NeverHttpClientFactory(), fake.Wrap())
            .ResolveAsync(Authenticated(), Ct);

        fake.LastRequest!.Action.Should().Be("read");
    }

    // 🔴 T-03: REST と gRPC が**同じ答え**を返す（並走中の正は REST。輸送で判定が変わらない）。
    [Fact]
    public async Task Rest_and_grpc_resolve_the_same_scope()
    {
        var rest = await new SearchAccessResolver(
            new StubHttpClientFactory(_ => Json(GrantedJson))).ResolveAsync(Authenticated(), Ct);

        var grpc = await new SearchAccessResolver(
            new NeverHttpClientFactory(), FakeAuthzScopeClient.Returning(Granted()).Wrap())
            .ResolveAsync(Authenticated(), Ct);

        grpc.Should().BeEquivalentTo(rest);
        rest.Granted.Should().BeTrue("★ 陽性対照 —— 両方 deny で一致したのではない");
    }

    // 🔴 T-04: **輸送の失敗はすべて deny-by-default**（REST の非 2xx・不達と同じ枝）。
    // 権限外文書の漏えいを防ぐため、fail-open は許されない。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task A_transport_failure_degrades_to_deny(StatusCode status)
    {
        var restDeny = await new SearchAccessResolver(new StubHttpClientFactory(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            .ResolveAsync(Authenticated(), Ct);

        var grpcDeny = await new SearchAccessResolver(
            new NeverHttpClientFactory(), FakeAuthzScopeClient.Failing(status).Wrap())
            .ResolveAsync(Authenticated(), Ct);

        grpcDeny.Should().BeEquivalentTo(restDeny);
        grpcDeny.Granted.Should().BeFalse();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class NeverHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("gRPC 経路では REST クライアントを作ってはならない");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
