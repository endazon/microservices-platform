using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Platform.Bff.Tests;

// FR-09, FR-17, SC-09, ADR-0004, NFR-28, [[IADR-0647]] (#2124): SC-09 の書き込み操作は監査ログに 1 件ずつ残る。
//
// 計画 SC-09 §アクセス制御「操作は監査ログに記録する」。13 口（ポリシー 4・属性 3・タグ 3・辺の型 3）のそれぞれで、
// action・利用者名・結果・対象 ID が 1 行に載ることを固定する。拒否（409 / 400）・後段不達も残す。
// 検証（dry-run）と読み取りは保存しないので残さない。detail に利用者の入力（名前）を載せない。
public class BffAbacAdminAuditTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string PolicyId = BffTestFactory.StubPolicyId.ToString();
    private static readonly string AttributeId = BffTestFactory.StubAttributeId.ToString();
    private static readonly string TagId = BffTestFactory.StubCreatedTagId.ToString();
    private static readonly string EdgeTypeId = BffTestFactory.StubEdgeTypeId.ToString();

    public BffAbacAdminAuditTests(BffTestFactory factory)
    {
        // 共有 fixture を既定へ戻してから観測する。
        _factory = factory;
        _factory.AuthzManagementStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
        _factory.TagWriteStatusCode = HttpStatusCode.OK;
        _factory.StubDeleteUsageCount = 0;
        _factory.EdgeTypeWriteStatusCode = HttpStatusCode.OK;
        _factory.EdgeTypeWriteStubBody = null;
        _factory.StubEdgeTypeWriteResult = null;
        _factory.StubEdgeTypeDeleteUsageCount = 0;
        _factory.RecordedAuditEntries.Clear();
        _factory.RecordedAudits.Clear();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        // GraphService のスタブは資格情報が無いと 401 を返す（辺の型）。他の後段にも同じものを転送する。
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private const string PolicyBody = """{"name":"秘密の名前","action":"read","userConditions":{},"documentConditions":{}}""";
    private const string AttributeBody = """{"key":"k","displayName":"秘密の名前","allowedValues":["a"],"isRequired":false,"scope":"document"}""";

    public static TheoryData<string, string, string?, string, string> WriteOperations => new()
    {
        { "POST", "/bff/admin/authz/policies", PolicyBody, "abac.policy.create", PolicyId },
        { "PUT", $"/bff/admin/authz/policies/{PolicyId}", PolicyBody, "abac.policy.update", PolicyId },
        { "PATCH", $"/bff/admin/authz/policies/{PolicyId}/active", """{"isActive":false}""", "abac.policy.set-active", PolicyId },
        { "DELETE", $"/bff/admin/authz/policies/{PolicyId}", null, "abac.policy.delete", PolicyId },
        { "POST", "/bff/admin/authz/attributes", AttributeBody, "abac.attribute.create", AttributeId },
        { "PUT", $"/bff/admin/authz/attributes/{AttributeId}", AttributeBody, "abac.attribute.update", AttributeId },
        { "DELETE", $"/bff/admin/authz/attributes/{AttributeId}", null, "abac.attribute.delete", AttributeId },
        { "POST", "/bff/tags", """{"name":"秘密の名前"}""", "tag.create", TagId },
        { "PUT", $"/bff/tags/{TagId}", """{"name":"秘密の名前"}""", "tag.rename", TagId },
        { "DELETE", $"/bff/tags/{TagId}", null, "tag.delete", TagId },
        { "POST", "/bff/edge-types", """{"name":"秘密の名前","layer":"recommended","isSymmetric":false}""", "edge-type.create", EdgeTypeId },
        { "PUT", $"/bff/edge-types/{EdgeTypeId}", """{"name":"秘密の名前"}""", "edge-type.rename", EdgeTypeId },
        { "DELETE", $"/bff/edge-types/{EdgeTypeId}", null, "edge-type.delete", EdgeTypeId },
    };

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? body)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) req.Content = Json(body);
        return await Client().SendAsync(req, Ct);
    }

    // AC: 書き込み 13 口のそれぞれで監査ログが 1 件だけ出る（action・利用者名・granted・対象 ID）。
    [Theory]
    [MemberData(nameof(WriteOperations))]
    public async Task 書き込みは監査ログに1件残る(string method, string path, string? body, string action, string target)
    {
        // 辺の型の作成・改名は、後段の応答に対象（ID）を載せる。
        _factory.StubEdgeTypeWriteResult = new EdgeTypeDto(BffTestFactory.StubEdgeTypeId, "refines", "recommended", false, false, 0);
        if (action == "edge-type.create") _factory.EdgeTypeWriteStatusCode = HttpStatusCode.Created;

        var resp = await SendAsync(method, path, body);

        ((int)resp.StatusCode).Should().BeInRange(200, 299, await resp.Content.ReadAsStringAsync(Ct));
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        entry.Action.Should().Be(action);
        entry.Subject.Should().Be("test-user");
        entry.Outcome.Should().Be("granted");
        entry.Detail.Should().Be($"target={target} status={(int)resp.StatusCode}");
        // NFR-28: 利用者の入力（名前など）は監査に載せない。
        entry.Detail.Should().NotContain("秘密の名前");
    }

    // AC: 後段が拒否（409 / 400）したときも 1 件残り、outcome は denied。
    [Fact]
    public async Task 削除の拒否はdeniedで残る()
    {
        _factory.StubDeleteUsageCount = 3;

        var resp = await SendAsync("DELETE", $"/bff/tags/{TagId}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("tag.delete", "denied", $"target={TagId} status=409"));
    }

    [Fact]
    public async Task 保存前検証の400はdeniedで残り作成の対象は読めない()
    {
        _factory.AuthzManagementStatusCode = HttpStatusCode.BadRequest;

        var resp = await SendAsync("POST", "/bff/admin/authz/policies", PolicyBody);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("abac.policy.create", "denied", "target=- status=400"));
    }

    // AC: 後段の 401 / 403 も「試みたが拒否された」として denied で残る（BFF 段の拒否とは status で読み分ける）。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task 後段の401と403はdeniedで残る(HttpStatusCode backend, int status)
    {
        _factory.AuthzManagementStatusCode = backend;

        var resp = await SendAsync("PUT", $"/bff/admin/authz/attributes/{AttributeId}", AttributeBody);

        ((int)resp.StatusCode).Should().Be(status);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("abac.attribute.update", "denied", $"target={AttributeId} status={status}"));
    }

    // AC: 後段に届かない（502 へ縮退）ときも failed で 1 件残る。
    [Fact]
    public async Task 後段不達はfailedで残る()
    {
        _factory.AuthzManagementThrows = true;

        var resp = await SendAsync("DELETE", $"/bff/admin/authz/policies/{PolicyId}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("abac.policy.delete", "failed", $"target={PolicyId} status=502"));
    }

    // AC: 検証（保存しない）と読み取りは記録しない。
    [Theory]
    [InlineData("POST", "/bff/admin/authz/policies/validate", PolicyBody)]
    [InlineData("GET", "/bff/admin/authz/policies", null)]
    [InlineData("GET", "/bff/admin/authz/attributes", null)]
    [InlineData("GET", "/bff/tags", null)]
    [InlineData("GET", "/bff/edge-types", null)]
    public async Task 検証と読み取りは記録しない(string method, string path, string? body)
    {
        var resp = await SendAsync(method, path, body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.RecordedAuditEntries.Should().BeEmpty();
    }
}
