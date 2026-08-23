using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Microsoft.AspNetCore.Http;

namespace GraphService.Api.Tests;

// FR-17, FR-05, ADR-0004, ADR-0034: **認可サービスが応えないときに deny へ縮退することを固定する。**
//
// グラフでは 1 文書の露出が近傍の存在まで明かすため、fail-open は特に許されない。
// WikiAccessResolver / RagOrchestrator と同一方針。
public class GraphAccessResolverTests
{
    private static HttpContext Ctx()
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "test-user"),
            new Claim("clearance", "internal"),
            new Claim("department", "sales"),
        ], "Test"));
        return ctx;
    }

    [Fact]
    public async Task Falls_back_to_deny_when_authorization_service_returns_500()
    {
        var resolver = new GraphAccessResolver(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var scope = await resolver.ResolveAsync(Ctx(), GraphAccessAction.Read, TestContext.Current.CancellationToken);

        scope.Granted.Should().BeFalse();
        scope.AllowedFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task Falls_back_to_deny_when_connection_fails()
    {
        var resolver = new GraphAccessResolver(
            new StubHttpClientFactory(_ => throw new HttpRequestException("refused")));

        var scope = await resolver.ResolveAsync(Ctx(), GraphAccessAction.Read, TestContext.Current.CancellationToken);

        scope.Granted.Should().BeFalse();
    }

    [Fact]
    public async Task Falls_back_to_deny_when_request_times_out()
    {
        var resolver = new GraphAccessResolver(
            new StubHttpClientFactory(_ => throw new TaskCanceledException("timeout")));

        var scope = await resolver.ResolveAsync(Ctx(), GraphAccessAction.Read, TestContext.Current.CancellationToken);

        scope.Granted.Should().BeFalse();
    }

    [Fact]
    public async Task Falls_back_to_deny_when_body_is_not_a_scope()
    {
        var resolver = new GraphAccessResolver(new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            }));

        var scope = await resolver.ResolveAsync(Ctx(), GraphAccessAction.Read, TestContext.Current.CancellationToken);

        scope.Granted.Should().BeFalse();
    }

    [Fact]
    public async Task Passes_clearance_and_department_claims_as_user_attributes()
    {
        HttpRequestMessage? captured = null;
        var resolver = new GraphAccessResolver(new StubHttpClientFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"userId":"test-user","allowedFilters":[],"granted":true}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }));

        var scope = await resolver.ResolveAsync(Ctx(), GraphAccessAction.Read, TestContext.Current.CancellationToken);

        scope.Granted.Should().BeTrue();
        captured.Should().NotBeNull();
        var body = await captured!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("clearance").And.Contain("department");
    }

    // 🔴 #993, IADR-0272 決定 4: **要求本文へ action が実際に載る。**
    // 載らなければ /authz/scope は既定の read を解決し、書き込み経路が読み取り権限で通る。
    [Theory]
    [InlineData(GraphAccessAction.Read, "read")]
    [InlineData(GraphAccessAction.Write, "write")]
    public async Task Sends_the_requested_action_in_the_scope_request(string action, string expected)
    {
        HttpRequestMessage? captured = null;
        var resolver = new GraphAccessResolver(new StubHttpClientFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"userId":"test-user","allowedFilters":[],"granted":true}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }));

        await resolver.ResolveAsync(Ctx(), action, TestContext.Current.CancellationToken);

        var body = await captured!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("action").GetString().Should().Be(expected);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 本文を後から読めるようにバッファへ移す。
            if (request.Content is not null)
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            return respond(request);
        }
    }
}
