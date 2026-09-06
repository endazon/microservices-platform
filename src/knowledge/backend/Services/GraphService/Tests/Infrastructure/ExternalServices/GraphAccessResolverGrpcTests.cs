using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-17, FR-05, UC-10, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0034, ADR-0036 D-07, ADR-0075,
// [[IADR-0272]] 決定 4, [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **T-P2-02** —— ABAC スコープ解決の REST 経路と gRPC 経路が同じ答えを返すことを固定する。
//
// 🔴 **同値は `read` と `write` の両方で測る。** `action` は既定値を持たない引数であり
// （[[IADR-0272]] 決定 4）、輸送を替えるときに落とすと**書き込み経路が読み取り権限で通る**。
// 片方の action でしか測らないと、その取り違えが緑のまま通る。
[Trait("TestKind", "Unit")]
public class GraphAccessResolverGrpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpContext Ctx()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim("clearance", "internal"),
                new Claim("department", "sales"),
            ], "Test")),
        };
        return ctx;
    }

    // 分岐つきの現実的なスコープ（キー単位 union ＋ 名前つき分岐）。
    private static AccessScopeResponse Granted() => new(
        "test-user",
        [new AttributeFilter("confidentiality", ["internal", "public"])],
        true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal", "public"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("owner", ["test-user"])]),
        ]);

    private static string GrantedJson =>
        System.Text.Json.JsonSerializer.Serialize(
            Granted(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    // T-P2-02: REST と gRPC が `Granted` / `AllowedFilters` / `Branches` で一致する（read / write とも）。
    [Theory]
    [InlineData(GraphAccessAction.Read)]
    [InlineData(GraphAccessAction.Write)]
    public async Task Rest_and_grpc_resolve_the_same_scope(string action)
    {
        var rest = await new GraphAccessResolver(
            new StubHttpClientFactory(_ => Json(GrantedJson))).ResolveAsync(Ctx(), action, Ct);

        var fake = FakeAuthzScopeClient.Returning(Granted());
        var grpc = await new GraphAccessResolver(
            new StubHttpClientFactory(_ => throw new InvalidOperationException("REST は呼ばれてはならない")),
            fake.Wrap()).ResolveAsync(Ctx(), action, Ct);

        grpc.Should().BeEquivalentTo(rest);
        // 陽性対照: 実際に gRPC を通っている（REST 側は例外を投げる器なので、通っていれば落ちている）。
        fake.CallCount.Should().Be(1);
    }

    // 🔴 `action` は gRPC の本文へ**そのまま**載る。既定へ丸めない。
    [Theory]
    [InlineData(GraphAccessAction.Read, "read")]
    [InlineData(GraphAccessAction.Write, "write")]
    public async Task Sends_the_requested_action_over_grpc(string action, string expected)
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        await new GraphAccessResolver(new NeverHttpClientFactory(), fake.Wrap())
            .ResolveAsync(Ctx(), action, Ct);

        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.Action.Should().Be(expected);
        fake.LastRequest.UserAttributes.Should().ContainKeys("clearance", "department");
    }

    // 縮退: gRPC の輸送失敗はすべて deny-by-default（REST の非 2xx・不達と同じ枝）。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Grpc_failure_degrades_to_deny(StatusCode status)
    {
        var restDeny = await new GraphAccessResolver(new StubHttpClientFactory(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            .ResolveAsync(Ctx(), GraphAccessAction.Read, Ct);

        var grpcDeny = await new GraphAccessResolver(
            new NeverHttpClientFactory(), FakeAuthzScopeClient.Failing(status).Wrap())
            .ResolveAsync(Ctx(), GraphAccessAction.Read, Ct);

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

    // gRPC 経路のとき REST が 1 度も使われないことを型で担保する器。
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
