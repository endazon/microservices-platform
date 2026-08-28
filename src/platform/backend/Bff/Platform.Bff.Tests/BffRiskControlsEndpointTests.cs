using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Platform.Bff.Tests;

// Issue #287, FR-14, IADR-0071: /bff/risk-controls/* が RiskManagementService へ pass-through すること、
// 認可を後段（OwnerOnly）へ委ね（匿名 401・非 owner の後段 403 を透過）、AST/SC-02/AST/SC-03 の 6 経路（settings・
// settings/history・settings/limits・settings/guard・status・stage-gate）を中継し、後段不達を 502 へ縮退し、
// 利用者トークンと PUT 本文を後段へ伝播することを検証する。BFF は DTO に結合しない（素の JSON を透過）。
public class BffRiskControlsEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffRiskControlsEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.RiskControlsStatusCode = HttpStatusCode.OK;
        _factory.RiskControlsThrows = false;
    }

    [Fact]
    public async Task GetSettings_WhenAuthenticated_Returns200WithPassThroughBody()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/risk-controls/settings", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<SettingsEnvelope>(TestContext.Current.CancellationToken);
        json!.Limits!.MaxOpenPositions.Should().Be(5);
    }

    [Fact]
    public async Task GetSettingsHistory_WhenAuthenticated_Returns200()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/risk-controls/settings/history", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("上限見直し");
    }

    [Fact]
    public async Task GetStatus_WhenAuthenticated_Returns200()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/risk-controls/status", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("killSwitchEngaged");
    }

    [Fact]
    public async Task GetStageGate_WhenAuthenticated_Returns200()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/risk-controls/stage-gate", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("currentStage");
    }

    [Fact]
    public async Task PutLimits_WhenAuthenticated_Returns200AndForwardsBody()
    {
        var client = _factory.CreateClient();
        var payload = new StringContent(
            "{\"limits\":{\"maxOpenPositions\":3},\"reason\":\"上限引き下げ\"}",
            Encoding.UTF8, "application/json");

        var resp = await client.PutAsync("/bff/risk-controls/settings/limits", payload, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // 後段へ本文がそのまま転送される（理由・上限を含む）。
        _factory.LastRiskControlsPutBody.Should().Contain("上限引き下げ");
        _factory.LastRiskControlsPutBody.Should().Contain("maxOpenPositions");
    }

    [Fact]
    public async Task PutGuard_WhenAuthenticated_Returns200AndForwardsBody()
    {
        var client = _factory.CreateClient();
        var payload = new StringContent(
            "{\"preventSameDayReentry\":true,\"reason\":\"ガード強化\"}",
            Encoding.UTF8, "application/json");

        var resp = await client.PutAsync("/bff/risk-controls/settings/guard", payload, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastRiskControlsPutBody.Should().Contain("ガード強化");
    }

    [Fact]
    public async Task PutLimits_WhenNonOwner_Passes403Through()
    {
        // 非 owner の変更は後段（OwnerOnly）が 403。BFF はそのまま透過する（BFF 側でロール制限しない）。
        _factory.RiskControlsStatusCode = HttpStatusCode.Forbidden;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/risk-controls/settings/limits", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutLimits_WhenValidationFails_Passes400Through()
    {
        _factory.RiskControlsStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/risk-controls/settings/limits", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutLimits_WhenVersionConflict_Passes409Through()
    {
        // 楽観排他の競合（設定の同時更新）は後段が 409。破壊的な自動再試行はしない（そのまま透過）。
        _factory.RiskControlsStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient()
            .PutAsync("/bff/risk-controls/settings/limits", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetSettings_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/bff/risk-controls/settings", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStatus_WhenBackendUnreachable_Returns502()
    {
        _factory.RiskControlsThrows = true;
        var resp = await _factory.CreateClient().GetAsync("/bff/risk-controls/status", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task GetSettings_PropagatesUserToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token-risk");

        await client.GetAsync("/bff/risk-controls/settings", TestContext.Current.CancellationToken);

        _factory.LastRiskControlsForwardedAuthorization.Should().Be("Bearer test-token-risk");
    }

    // BFF は DTO に結合しないため、テスト側でのみ透過本文の一部を型付けして検証する。
    private sealed record SettingsEnvelope(LimitsBody? Limits);
    private sealed record LimitsBody(int MaxOpenPositions);
}
