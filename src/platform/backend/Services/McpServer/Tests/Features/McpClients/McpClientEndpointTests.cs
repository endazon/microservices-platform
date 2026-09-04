using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;

namespace McpServer.Tests.Features.McpClients;

// FR-16, UC-09: MCP クライアント登録管理 API。管理者限定である
// （画面そのものは本作業の射程外であり、ここで検査するのはバックエンド API の統制だけである）。
[Trait("TestKind", "Integration")]
public class McpClientEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Admin() => factory.CreateClient();

    private HttpClient NonAdmin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        return client;
    }

    // FR-16, UC-09: 登録・一覧・無効化が一通り動く（基本フロー）。
    [Fact]
    public async Task クライアントを登録し無効化できる()
    {
        var client = Admin();
        var created = await client.PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("agent-a", "エージェントA", "interactive"), TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", TestContext.Current.CancellationToken);
        list.Should().Contain(c => c.ClientId == "agent-a" && c.Enabled);

        var disabled = await client.PostAsync("/mcp-clients/agent-a/disable", null, TestContext.Current.CancellationToken);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disabled.Content.ReadFromJsonAsync<McpClientView>(TestContext.Current.CancellationToken))!.Enabled.Should().BeFalse();
    }

    // FR-16, UC-09: 管理系は管理者限定である（計画の画面定義が「管理者限定」と定めている）。
    [Fact]
    public async Task 管理者以外は登録管理にアクセスできない()
    {
        var response = await NonAdmin().GetAsync("/mcp-clients", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 🔴 FR-16: サービスアカウントへ個人資料を読ませる属性割当は API でも拒否する
    // （計画 ADR-0024 2026-08-02 注記 / ADR-0034 決定 9）。構成検証と同じ規則を API 面でも通す。
    [Fact]
    public async Task サービスアカウントへ個人資料の属性割当は拒否される()
    {
        var response = await Admin().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("batch-b", "無人B", "service-account",
                new Dictionary<string, string> { ["doc_scope"] = "private-note" }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("private-note");
    }

    // FR-16（陽性対照）: 個人資料以外の属性割当は通る。
    [Fact]
    public async Task サービスアカウントへ組織文書の属性割当は通る()
    {
        var response = await Admin().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("batch-c", "無人C", "service-account",
                new Dictionary<string, string> { ["doc_scope"] = "organization" }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 🔴 FR-16: 属性の差し替え経路でも同じ規則が効く（登録時だけ塞いでも意味がない）。
    [Fact]
    public async Task 属性差し替えでも個人資料の割当は拒否される()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("batch-d", "無人D", "service-account"), TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync("/mcp-clients/batch-d/attributes",
            new ReplaceMcpClientAttributesRequest(
                new Dictionary<string, string> { ["doc_scope"] = "private-note" }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-16, UC-09: 公開ツール一覧の確認。公開構成が空なら 0 件（既定は非公開）。
    [Fact]
    public async Task 公開ツール一覧を取得できる()
    {
        var view = await Admin().GetFromJsonAsync<EffectiveToolsView>("/mcp-clients/tools", TestContext.Current.CancellationToken);
        view.Should().NotBeNull();
        view!.Tools.Should().BeEmpty();
    }

    // FR-16: 不正な kind は拒否する。
    [Fact]
    public async Task 不正な種別は拒否される()
    {
        var response = await Admin().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("agent-x", "X", "robot"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
