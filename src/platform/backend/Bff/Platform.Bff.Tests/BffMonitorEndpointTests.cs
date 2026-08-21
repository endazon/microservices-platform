using AwesomeAssertions;
using System.Net;
using System.Text;

namespace Platform.Bff.Tests;

// Issue #288, FR-14, IADR-0072: /bff/monitor/* が MarketMonitorService へ pass-through すること、
// 認可を後段（OwnerOnly）へ委ね（匿名 401・非 owner の後段 403 を透過）、AST/SC-02 watchlist UI の 4 経路
// （watchlist GET/POST/DELETE・watchlist/history GET）を中継し、後段不達を 502 へ縮退し、利用者トークンと
// POST/DELETE 本文を後段へ伝播することを検証する。BFF は DTO に結合しない（素の JSON を透過）。
public class BffMonitorEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffMonitorEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.MonitorStatusCode = HttpStatusCode.OK;
        _factory.MonitorThrows = false;
    }

    [Fact]
    public async Task GetWatchlist_WhenAuthenticated_Returns200WithPassThroughBody()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/monitor/watchlist");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("7203");
    }

    [Fact]
    public async Task GetWatchlistHistory_WhenAuthenticated_Returns200()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/monitor/watchlist/history");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("監視追加");
    }

    [Fact]
    public async Task PostWatchlist_WhenAuthenticated_Returns200AndForwardsBody()
    {
        var client = _factory.CreateClient();
        var payload = new StringContent(
            "{\"symbol\":\"9984\",\"market\":\"TSE\",\"reason\":\"監視追加\"}",
            Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/bff/monitor/watchlist", payload);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // 後段へ本文がそのまま転送される（銘柄・理由を含む）。
        _factory.LastMonitorForwardedBody.Should().Contain("9984");
        _factory.LastMonitorForwardedBody.Should().Contain("監視追加");
    }

    [Fact]
    public async Task DeleteWatchlist_WhenAuthenticated_Returns200AndForwardsDeleteBody()
    {
        // 差分検証（IADR-0072 決定3）: DELETE /monitor/watchlist は本文（銘柄・理由）を取るため、
        // BFF は DELETE でもリクエスト本文を後段へ転送しなければならない。
        var client = _factory.CreateClient();
        var payload = new StringContent(
            "{\"symbol\":\"7203\",\"market\":\"TSE\",\"reason\":\"監視解除\"}",
            Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Delete, "/bff/monitor/watchlist") { Content = payload };

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastMonitorDeleteBody.Should().NotBeNull();
        _factory.LastMonitorDeleteBody.Should().Contain("7203");
        _factory.LastMonitorDeleteBody.Should().Contain("監視解除");
    }

    [Fact]
    public async Task PostWatchlist_WhenNonOwner_Passes403Through()
    {
        // 非 owner の変更は後段（OwnerOnly）が 403。BFF はそのまま透過する（BFF 側でロール制限しない）。
        _factory.MonitorStatusCode = HttpStatusCode.Forbidden;
        var resp = await _factory.CreateClient()
            .PostAsync("/bff/monitor/watchlist", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostWatchlist_WhenValidationFails_Passes400Through()
    {
        // 重複追加・空・未定義 market は後段 400（AST#191）。破壊的な自動再試行はしない（そのまま透過）。
        _factory.MonitorStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient()
            .PostAsync("/bff/monitor/watchlist", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostWatchlist_WhenConflict_Passes409Through()
    {
        _factory.MonitorStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient()
            .PostAsync("/bff/monitor/watchlist", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetWatchlist_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/bff/monitor/watchlist");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWatchlist_WhenBackendUnreachable_Returns502()
    {
        _factory.MonitorThrows = true;
        var resp = await _factory.CreateClient().GetAsync("/bff/monitor/watchlist");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task GetWatchlist_PropagatesUserToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token-monitor");

        await client.GetAsync("/bff/monitor/watchlist");

        _factory.LastMonitorForwardedAuthorization.Should().Be("Bearer test-token-monitor");
    }
}
