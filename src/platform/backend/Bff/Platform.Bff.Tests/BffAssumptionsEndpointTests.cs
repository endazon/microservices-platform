using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Platform.Bff.Tests;

// Issue #283, FR-17, UC-06, IADR-0070: /bff/assumptions が ConfigurationService へ pass-through すること、
// 認可を後段へ委ね（匿名 401・非 owner の後段 403 を透過）、GET/PUT/history を中継し、後段不達を 502 へ縮退し、
// 利用者トークンと PUT 本文を後段へ伝播することを検証する。BFF は DTO に結合しない（素の JSON を透過）。
public class BffAssumptionsEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffAssumptionsEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.AssumptionsStatusCode = HttpStatusCode.OK;
        _factory.AssumptionsThrows = false;
    }

    [Fact]
    public async Task Get_WhenAuthenticated_Returns200WithPassThroughBody()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/assumptions");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<AssumptionsEnvelope>();
        json!.Version.Should().Be(1);
        json.Assumptions!.CapitalGainsTaxRate.Should().Be(0.20315);
    }

    [Fact]
    public async Task GetHistory_WhenAuthenticated_Returns200()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/assumptions/history");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("初期値");
    }

    [Fact]
    public async Task Put_WhenAuthenticated_Returns200AndForwardsBody()
    {
        var client = _factory.CreateClient();
        var payload = new StringContent(
            "{\"assumptions\":{\"capitalGainsTaxRate\":0.30},\"expectedVersion\":1,\"reason\":\"税率見直し\"}",
            Encoding.UTF8, "application/json");

        var resp = await client.PutAsync("/bff/assumptions", payload);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // 後段へ本文がそのまま転送される（理由・期待バージョンを含む）。
        _factory.LastAssumptionsPutBody.Should().Contain("税率見直し");
        _factory.LastAssumptionsPutBody.Should().Contain("expectedVersion");
    }

    [Fact]
    public async Task Put_WhenNonOwner_Passes403Through()
    {
        // 非 owner の変更は後段（OwnerOnly）が 403。BFF はそのまま透過する（BFF 側でロール制限しない）。
        _factory.AssumptionsStatusCode = HttpStatusCode.Forbidden;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/assumptions", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Put_WhenValidationFails_Passes400Through()
    {
        _factory.AssumptionsStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/assumptions", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WhenVersionConflict_Passes409Through()
    {
        // 楽観排他の競合（ExpectedVersion 不一致）は後段が 409。破壊的な自動再試行はしない（そのまま透過）。
        _factory.AssumptionsStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/assumptions", new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/bff/assumptions");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WhenBackendUnreachable_Returns502()
    {
        _factory.AssumptionsThrows = true;
        var resp = await _factory.CreateClient().GetAsync("/bff/assumptions");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Get_PropagatesUserToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token-abc");

        await client.GetAsync("/bff/assumptions");

        _factory.LastAssumptionsForwardedAuthorization.Should().Be("Bearer test-token-abc");
    }

    // BFF は DTO に結合しないため、テスト側でのみ透過本文の一部を型付けして検証する。
    private sealed record AssumptionsEnvelope(AssumptionsBody? Assumptions, int Version, bool IsResolved);
    private sealed record AssumptionsBody(double CapitalGainsTaxRate);
}
