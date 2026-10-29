using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Platform.Bff.Tests;

// FR-09, SC-17, ADR-0116 決定 1, NFR-28, [[IADR-0647]] (#2298): SC-17 の書き込み 5 口は監査ログに 1 件ずつ残る。
//
// 計画 §SC-17 アクション「操作は監査ログに記録」。属性・部門・ロールの差し替え、無効化・有効化のそれぞれで、
// action・利用者名・結果・対象の利用者 ID が 1 行に載ることを固定する。拒否（400）・後段不達（502）も残す。
// 読み取りは残さない。detail に利用者の入力（属性値・部門・ロール名）を載せない。
public class BffUserAdminAuditTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserId = "u-tanaka";
    // 入力値は監査に出てはならない。どれも detail に無いことを見る。
    private const string SecretValue = "secret-input-value";

    public BffUserAdminAuditTests(BffTestFactory factory)
    {
        // 共有 fixture を既定へ戻してから観測する。
        _factory = factory;
        _factory.UserAdminStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
        _factory.RecordedAuditEntries.Clear();
        _factory.RecordedAudits.Clear();
    }

    private HttpClient AsAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    public static TheoryData<string, string, string?, string> WriteOperations => new()
    {
        { "PUT", $"/bff/admin/users/{UserId}/attributes", "{\"attributes\":{\"clearance\":\"" + SecretValue + "\"}}", "user.attributes.replace" },
        { "PUT", $"/bff/admin/users/{UserId}/department", "{\"department\":\"" + SecretValue + "\"}", "user.department.replace" },
        { "PUT", $"/bff/admin/users/{UserId}/roles", "{\"roles\":[\"" + SecretValue + "\"]}", "user.roles.replace" },
        { "POST", $"/bff/admin/users/{UserId}/disable", null, "user.disable" },
        { "POST", $"/bff/admin/users/{UserId}/enable", null, "user.enable" },
    };

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? body)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await AsAdmin().SendAsync(req, Ct);
    }

    // AC: 書き込み 5 口のそれぞれで監査ログが 1 件だけ出る（action・利用者名・granted・対象の利用者 ID）。入力値は載らない。
    [Theory]
    [MemberData(nameof(WriteOperations))]
    public async Task 書き込みは監査ログに1件残る(string method, string path, string? body, string action)
    {
        var resp = await SendAsync(method, path, body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        entry.Action.Should().Be(action);
        entry.Subject.Should().Be("test-user");
        entry.Outcome.Should().Be("granted");
        entry.Detail.Should().Be($"target={UserId} status=200");
        // NFR-28: 利用者の入力（属性値・部門・ロール名）は監査に載せない。
        entry.Detail.Should().NotContain(SecretValue);
    }

    // AC: 後段が拒否（400 検証エラー）したときも 1 件残り、outcome は denied。
    [Fact]
    public async Task 後段の拒否はdeniedで残る()
    {
        _factory.UserAdminStatusCode = HttpStatusCode.BadRequest;

        var resp = await SendAsync("PUT", $"/bff/admin/users/{UserId}/roles", """{"roles":[]}""");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("user.roles.replace", "denied", $"target={UserId} status=400"));
    }

    // AC: 後段に届かない（502 へ縮退）ときも failed で 1 件残る。
    [Fact]
    public async Task 後段不達はfailedで残る()
    {
        _factory.AuthzManagementThrows = true;

        var resp = await SendAsync("POST", $"/bff/admin/users/{UserId}/disable", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var entry = _factory.RecordedAuditEntries.Should().ContainSingle().Which;
        (entry.Action, entry.Outcome, entry.Detail).Should().Be(("user.disable", "failed", $"target={UserId} status=502"));
    }

    // 読み取り（一覧・選択肢・部門の読み取り）は保存しないので残さない（陽性対照は上の書き込み）。
    [Theory]
    [InlineData("/bff/admin/users")]
    [InlineData("/bff/admin/users/assignable-roles")]
    [InlineData($"/bff/admin/users/{UserId}/department")]
    public async Task 読み取りは監査しない(string path)
    {
        var resp = await AsAdmin().GetAsync(path, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.RecordedAuditEntries.Should().BeEmpty();
    }
}
