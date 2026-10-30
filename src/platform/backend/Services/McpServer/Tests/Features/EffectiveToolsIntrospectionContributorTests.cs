using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients.ListEffectiveTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace McpServer.Tests.Features;

// FR-15, FR-16, ADR-0024 §5, IADR-0660 (#2285): 実効ツール一覧を自己申告へ寄与する。カタログの更新は次の要求に反映される。
[Trait("TestKind", "Unit")]
public class EffectiveToolsIntrospectionContributorTests
{
    private static McpToolDeclaration Decl(string name, string scope = "read", string egressClass = "internal") =>
        new(name, "説明", """{"type":"object"}""", scope, egressClass);

    private static readonly ServiceIntrospectionDto StaticReport = new("mcp-server", [], [], []);

    [Fact]
    public async Task カタログの実効ツールを公開名の順に寄与し_更新は次の要求に反映される()
    {
        var catalog = new ToolCatalog(NullLogger<ToolCatalog>.Instance);
        IIntrospectionContributor[] contributors = [new EffectiveToolsIntrospectionContributor(catalog)];

        // 収集の前は空（静的な申告のまま）。
        var before = await IntrospectionContributions.ApplyAsync(StaticReport, contributors, TestContext.Current.CancellationToken);
        before.McpTools.Should().BeEmpty();

        catalog.Refresh(
            new ToolPublicationConfig("v1",
            [
                new ToolPublicationEntry("retrieval.search", "retrieval"),
                new ToolPublicationEntry("graph.traverse", "graph"),
            ]),
            [
                new ServiceToolDeclarations("retrieval", [Decl("retrieval.search", "read", "internal"), Decl("retrieval.hidden")]),
                new ServiceToolDeclarations("graph", [Decl("graph.traverse", "graph:read", "restricted")]),
            ]);

        var after = await IntrospectionContributions.ApplyAsync(StaticReport, contributors, TestContext.Current.CancellationToken);
        after.McpTools.Should().Equal(
            new McpToolDto("graph.traverse", "graph", "graph:read", "restricted"),
            new McpToolDto("retrieval.search", "retrieval", "read", "internal"));
        after.Service.Should().Be("mcp-server", "静的な申告の残りは変えない");

        // 公開構成から外すと、次の要求から消える。
        catalog.Refresh(
            new ToolPublicationConfig("v2", [new ToolPublicationEntry("graph.traverse", "graph")]),
            [new ServiceToolDeclarations("graph", [Decl("graph.traverse", "graph:read", "restricted")])]);
        (await IntrospectionContributions.ApplyAsync(StaticReport, contributors, TestContext.Current.CancellationToken))
            .McpTools.Select(t => t.PublishedName).Should().Equal("graph.traverse");
    }
}

// 本番の Program.cs が寄与を登録している（登録が無ければ構成情報 API に実効ツールが 1 件も出ない）。
[Trait("TestKind", "Integration")]
public class EffectiveToolsIntrospectionRegistrationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void 本番の配線は実効ツールの寄与を登録する()
    {
        factory.Services.GetServices<IIntrospectionContributor>()
            .Should().ContainSingle(c => c is EffectiveToolsIntrospectionContributor);
    }
}
