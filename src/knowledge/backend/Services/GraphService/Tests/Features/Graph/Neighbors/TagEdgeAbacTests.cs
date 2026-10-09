using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph.Neighbors;

// FR-17, ADR-0034 決定 2・3, IADR-0242, [[IADR-0521]] (#1396): **共有タグの辺も、ホップごとの判定を同じ型ゲートで通る。**
//
// 共有タグの辺は既存の `edges` 表に入り、新しい読み取り経路を持たない。ここではそれを外から確かめる ——
// 閲覧権の無い文書と同じタグを持っていても、その文書・その辺・件数のいずれにも現れないこと。
[Trait("TestKind", "Integration")]
public class TagEdgeAbacTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TagEdgeAbacTests(TestWebApplicationFactory factory) => _factory = factory;

    private static GraphDocument Node(Guid id, string confidentiality)
        => GraphDocument.Create(id, $"n-{id:N}",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality }, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 閲覧権の無い文書との共有タグの辺は近傍にも件数にも現れない()
    {
        var origin = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var forbidden = Guid.NewGuid();
        var type = EdgeType.Create($"related-{Guid.NewGuid():N}", EdgeTypeLayer.Core, isSymmetric: true);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(origin, "internal"));
            db.Documents.Add(Node(visible, "internal"));
            db.Documents.Add(Node(forbidden, "restricted"));
            db.Edges.Add(Edge.Create(origin, visible, type.Id, true, EdgeProvenance.Auto,
                autoSource: EdgeAutoSource.Tag));
            db.Edges.Add(Edge.Create(origin, forbidden, type.Id, true, EdgeProvenance.Auto,
                autoSource: EdgeAutoSource.Tag));
            return Task.CompletedTask;
        });
        _factory.ScopeProvider = _ => new AccessScopeResponse(
            "test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

        var res = await _factory.CreateClient().GetAsync(
            $"/graph/{origin}/neighbors", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await res.Content.ReadFromJsonAsync<GraphViewDto>(TestContext.Current.CancellationToken);
        view!.Nodes.Select(n => n.DocumentId).Should().BeEquivalentTo([origin, visible],
            "陽性対照: 見える相手との共有タグの辺は辿れる");
        view.Edges.Should().ContainSingle("権限外の端点を持つ辺は返さない");
        view.TotalEdges.Should().Be(1, "件数にも出さない（存在を漏らさない）");
        view.TotalNodes.Should().Be(2);
    }
}
