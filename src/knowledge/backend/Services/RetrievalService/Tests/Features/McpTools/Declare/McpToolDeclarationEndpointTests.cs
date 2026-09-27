using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Text.Json;
using RetrievalService.Features.McpTools.Declare;

namespace RetrievalService.Tests.Features.McpTools.Declare;

// FR-16, FR-19, ADR-0024 §2・2026-08-02 注記, ADR-0034 決定 9 (#1020):
// ツール定義の自己申告（`GET /internal/mcp-tools`）。
//
// 🔴 **陽性対照を必ず対で置く。** 「個人資料が除外される」だけを測ると、
// **常に空を返す実装がテストを通る** —— #1020 が是正しようとしている状態そのものである。
[Trait("TestKind", "Integration")]
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

    // FR-16（陽性対照）: **申告した個々のツールが載る。** 空でないことだけを測らない。
    // 名前は計画（11_mcp-server-integration §2）が例示したものと一致させる。
    [Fact]
    public async Task 検索系のツールを申告する()
    {
        var declared = await DeclarationsAsync();

        declared.Service.Should().Be("retrieval-service");
        declared.Tools.Select(t => t.Name).Should().Contain("retrieval.search_documents");
    }

    // FR-16, ADR-0024 §5, ADR-0117 決定 1: 5 項目がすべて埋まっている。
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
            tool.RequiredScope.Should().NotBeNullOrWhiteSpace();
            tool.EgressClass.Should().NotBeNullOrWhiteSpace();
        }
    }

    // 🔴 FR-19, FR-21 ⑨, ADR-0034 決定 9（否定形）:
    // 索引には所有者が opt-in した個人資料が載り得る（[[IADR-0283]] 決定 3）。
    // それでも **MCP へ出す検索ツールは組織文書に限る**ものとして申告する。
    [Fact]
    public void 申告するツールはいずれも個人資料を対象にしない()
    {
        McpToolDeclarationSource.Candidates()
            .Should().NotBeEmpty("候補が空なら本試験は何も測っていない")
            .And.OnlyContain(c => !DocumentScopes.IsPrivateNote(c.Coverage));
    }

    // FR-19, ADR-0054 §結果（否定形）: 個人資料スコープの候補は落ちる。
    [Fact]
    public void 選別は個人資料スコープの候補を落とす()
    {
        var candidates = new List<McpToolCandidate>
        {
            Candidate("retrieval.search_private_notes", DocumentScopes.PrivateNote),
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
            Candidate("retrieval.search_documents", DocumentScopes.Organization),
            Candidate("retrieval.list_collections", scope: null),
        };

        McpToolDeclarationSource.Publishable(candidates).Select(d => d.Name)
            .Should().Equal("retrieval.search_documents", "retrieval.list_collections");
    }

    // 🔴 FR-16, ADR-0117 決定 1（#1516）: **申告は実行先の URL を持たない。** 規約は 5 項目（`endpoint` は外した）。
    // 実行先は McpServer が「申告したサービス＋ツール名」で決める。申告の JSON に URL が戻ると、申告元が別のサービスの
    // 内部経路を自分のツールとして申告できる形が戻る。ワイヤ（JSON のキー）で測る —— DTO で読むと未知のキーは読み飛ばされて見えない。
    [Fact]
    public async Task 申告は実行先のURLを持たない()
    {
        var json = await factory.CreateClient()
            .GetStringAsync(McpToolEndpoints.ToolsPath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var tools = document.RootElement.GetProperty("tools").EnumerateArray().ToList();

        tools.Should().NotBeEmpty("陽性対照 —— 空の申告で緑にしない");
        tools.Should().AllSatisfy(t => t.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["name", "description", "input_schema", "required_scope", "egress_class"],
            "ツール定義規約は 5 項目であり、実行先の URL（旧 endpoint）を申告しない"));
    }

    private static McpToolCandidate Candidate(string name, string? scope) => new(
        scope is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [DocumentScopes.Key] = scope },
        new McpToolDeclaration(name, "説明", """{"type":"object"}""",
            "retrieval:search", "internal"));
}
