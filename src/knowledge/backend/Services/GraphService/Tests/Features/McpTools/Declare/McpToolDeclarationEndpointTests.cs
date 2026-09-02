using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Features.McpTools.Declare;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Configuration;

namespace GraphService.Tests.Features.McpTools.Declare;

// FR-16, FR-17, FR-19, ADR-0024 §2・2026-08-01 注記・2026-08-02 注記, ADR-0034 決定 9 (#1020):
// ツール定義の自己申告（`GET /internal/mcp-tools`）。
//
// 🔴 **陽性対照を必ず対で置く。** 「個人資料が除外される」「要約系が現れない」だけを測ると、
// **常に空を返す実装がテストを通る** —— #1020 が是正しようとしている状態そのものである。
public class McpToolDeclarationEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private async Task<ServiceToolDeclarations> DeclarationsAsync()
    {
        var res = await factory.CreateClient()
            .GetAsync(McpToolEndpoints.ToolsPath, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var declared = await res.Content.ReadFromJsonAsync<ServiceToolDeclarations>(
            TestContext.Current.CancellationToken);
        declared.Should().NotBeNull();
        return declared!;
    }

    // FR-17（陽性対照）: 11_mcp-server-integration §6 の表が「公開する」と定めた探索系 3 つを申告する。
    [Fact]
    public async Task 探索系の三つのツールを申告する()
    {
        var declared = await DeclarationsAsync();

        declared.Service.Should().Be("graph-service");
        declared.Tools.Select(t => t.Name).Should()
            .Contain(["graph.get_backlinks", "graph.get_links", "graph.traverse"]);
    }

    // 🔴 FR-17, ADR-0024 §決定（否定形）: **要約系（`get_cluster_summary`）は公開しない。**
    // LLM 呼び出しを伴うものを外部エージェントへ出さない方針（AI 分析系を初期公開に含めない）の適用結果。
    [Fact]
    public async Task 要約系のツールは申告に現れない()
    {
        var declared = await DeclarationsAsync();

        declared.Tools.Select(t => t.Name).Should()
            .NotContain(n => n.EndsWith("get_cluster_summary", StringComparison.Ordinal));
        declared.Tools.Select(t => t.Name).Should()
            .NotContain(n => n.StartsWith("ai.", StringComparison.Ordinal));
    }

    // FR-17, 11_mcp-server-integration §6: `hops` の既定 2・上限 3 を入力スキーマへ写す。
    // **上限を丸めずエラーで拒否する**のは実行口の責務だが、規約の側に上限が出ていないと
    // 呼び出し側の LLM は上限の存在を知らずに 4 を投げる。
    [Fact]
    public async Task 探索ツールはホップ数の既定と上限を申告する()
    {
        var declared = await DeclarationsAsync();

        var traverse = declared.Tools.Single(t => t.Name == "graph.traverse");
        traverse.InputSchema.Should().Contain("\"maximum\":3").And.Contain("\"default\":2");
    }

    // FR-16, ADR-0024 §5: 6 項目がすべて埋まっている。
    [Fact]
    public async Task 申告の各項目が埋まっている()
    {
        var declared = await DeclarationsAsync();

        declared.Tools.Should().NotBeEmpty();
        foreach (var tool in declared.Tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.InputSchema.Should().NotBeNullOrWhiteSpace();
            tool.Endpoint.Should().NotBeNullOrWhiteSpace();
            tool.RequiredScope.Should().NotBeNullOrWhiteSpace();
            tool.EgressClass.Should().NotBeNullOrWhiteSpace();
        }
    }

    // 🔴 FR-19, ADR-0034 決定 9（否定形）: 申告するツールはいずれも個人資料を対象にしない。
    [Fact]
    public void 申告するツールはいずれも個人資料を対象にしない()
    {
        McpToolDeclarationSource.Candidates("http://localhost")
            .Should().NotBeEmpty("候補が空なら本試験は何も測っていない")
            .And.OnlyContain(c => !DocumentScopes.IsPrivateNote(c.Coverage));
    }

    // FR-19, ADR-0054 §結果（否定形）: 個人資料スコープの候補は落ちる。
    [Fact]
    public void 選別は個人資料スコープの候補を落とす()
    {
        var candidates = new List<McpToolCandidate>
        {
            Candidate("graph.traverse_private_notes", DocumentScopes.PrivateNote),
        };

        McpToolDeclarationSource.Publishable(candidates).Should().BeEmpty();
    }

    // 🔴 FR-19, ADR-0054 §結果（陽性対照）: 組織スコープ・**スコープを持たない**候補は残る。
    // 判定を否定（`!= "organization"`）で書き換えるとスコープ無しの候補が落ちてここが赤くなる。
    [Fact]
    public void 選別は個人資料でない候補を残す()
    {
        var candidates = new List<McpToolCandidate>
        {
            Candidate("graph.get_links", DocumentScopes.Organization),
            Candidate("graph.get_backlinks", scope: null),
        };

        McpToolDeclarationSource.Publishable(candidates).Select(d => d.Name)
            .Should().Equal("graph.get_links", "graph.get_backlinks");
    }

    // FR-16: 実行口の基底 URL は構成で上書きできる（既定はメッシュ内の自サービス URL）。
    [Fact]
    public void 実行口の基底URLを構成で上書きできる()
    {
        McpToolDeclarationSource.SelfBaseUrl(new ConfigurationBuilder().Build())
            .Should().Be(McpToolDeclarationSource.DefaultSelfBaseUrl);

        var overridden = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [McpToolDeclarationSource.SelfBaseUrlKey] = "http://graph-service.mesh:9090",
            }).Build();

        McpToolDeclarationSource.Declare(overridden).Tools
            .Should().OnlyContain(t => t.Endpoint.StartsWith("http://graph-service.mesh:9090/internal/mcp/"));
    }

    private static McpToolCandidate Candidate(string name, string? scope) => new(
        scope is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [DocumentScopes.Key] = scope },
        new McpToolDeclaration(name, "説明", """{"type":"object"}""",
            "http://graph-service:8080/internal/mcp/x", "graph:read", "internal"));
}
