using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Platform.Bff.Tests;

// FR-09, UC-05, SC-09, IADR-0040: /bff/admin/authz が AuthorizationService の管理 API へ AdminOnly で
// 中継し、非管理者は 403・無認証は 401、保存前検証エラー（400）・属性参照競合（409）を透過することを検証する。
public class BffAuthzEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffAuthzEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.AuthzManagementStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ListPolicies_AsAdmin_ReturnsPolicies()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/admin/authz/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<AbacPolicyDto>>();
        body!.Should().ContainSingle(p => p.Name == "社員は社内文書を閲覧可");
    }

    [Fact]
    public async Task ListAttributes_AsAdmin_ReturnsAttributes()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/admin/authz/attributes");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<AttributeDefinitionDto>>();
        body!.Should().ContainSingle(a => a.Key == "confidentiality");
    }

    [Fact]
    public async Task ListPolicies_AsNonAdmin_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync("/bff/admin/authz/policies");

        // SC-09 は platform-admin のみ（operator も不可）。
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListPolicies_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.GetAsync("/bff/admin/authz/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePolicy_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsync("/bff/admin/authz/policies",
            Json("""{"name":"p","action":"read","userConditions":{},"documentConditions":{}}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreatePolicy_WhenValidationFails_Passes400Through()
    {
        // 後段（AuthorizationService）の保存前検証エラーを透過する（検証結果を画面が表示できる）。
        _factory.AuthzManagementStatusCode = HttpStatusCode.BadRequest;
        var resp = await _factory.CreateClient().PostAsync("/bff/admin/authz/policies",
            Json("""{"name":"","action":"nope","userConditions":{},"documentConditions":{}}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-05, FR-09, SC-09, #535: dry-run 検証を中継する。
    //
    // **矛盾は 200 ＋ `{ valid, errors }`** であり、保存の 400 とは別物である
    // （BFF は判断せず透過する。[[IADR-0040]] 決定 2）。
    [Fact]
    public async Task ValidatePolicy_AsAdmin_Returns200()
    {
        var resp = await _factory.CreateClient().PostAsync("/bff/admin/authz/policies/validate",
            Json("""{"name":"検証","action":"read","userConditions":{},"documentConditions":{}}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // SC-09 は platform-admin のみ（operator も不可）。**検証も管理操作である。**
    [Fact]
    public async Task ValidatePolicy_AsNonAdmin_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await client.PostAsync("/bff/admin/authz/policies/validate",
            Json("""{"name":"検証","action":"read","userConditions":{},"documentConditions":{}}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateAttribute_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsync("/bff/admin/authz/attributes",
            Json("""{"key":"department","label":"部門","allowedValues":["hr","sales"],"required":false,"scope":"document"}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeleteAttribute_WhenReferenced_Passes409Through()
    {
        // 参照中の属性削除は 409（IADR-0006）。透過する。
        _factory.AuthzManagementStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient()
            .DeleteAsync($"/bff/admin/authz/attributes/{BffTestFactory.StubAttributeId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ListPolicies_WhenBackendUnreachable_Returns502()
    {
        // 後段（AuthorizationService）不達は 502 へ縮退する（例外フローの明示検証・レビュー #170 指摘対応）。
        _factory.AuthzManagementThrows = true;
        var resp = await _factory.CreateClient().GetAsync("/bff/admin/authz/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task SetPolicyActive_AsAdmin_Succeeds()
    {
        var resp = await _factory.CreateClient().PatchAsync(
            $"/bff/admin/authz/policies/{BffTestFactory.StubPolicyId}/active",
            Json("""{"isActive":false}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
