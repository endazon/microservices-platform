using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Domain;

// FR-17, UC-10, ADR-0034 決定 2, IADR-0242 決定 2・6: **出力ゲートの意味論を固定する。**
//
// `Seal` は「未フィルタが外へ出ない」ことを担保する多層防御の 2 段目である。入口のゲート
// （`AuthorizedNode`）を通っていれば結果は変わらないが、**迂回経路があっても出口で必ず濾される**
// ことをここで固定する。
public class AuthorizedGraphViewTests
{
    private static GraphDocument Doc(Guid id, string confidentiality)
        => GraphDocument.Create(id, "d",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            null, DateTimeOffset.UtcNow);

    private static AccessScopeResponse InternalOnly()
        => new("u", [new AttributeFilter("confidentiality", ["internal"])], true);

    // FR-05: 許可ポリシーが無ければ、未フィルタで何が入っていても外へ出さない。
    [Fact]
    public void Seal_returns_nothing_when_scope_is_not_granted()
    {
        var a = Guid.NewGuid();
        var sub = new UnfilteredSubgraph([Doc(a, "public")], [], false);

        var view = GraphViewResponse.Seal(sub, new AccessScopeResponse("u", [], false));

        view.Nodes.Should().BeEmpty();
        view.Edges.Should().BeEmpty();
    }

    // 🔴 ADR-0034 決定 2: **迂回して未フィルタのノードを詰めても、出口で濾される。**
    [Fact]
    public void Seal_drops_unauthorized_nodes_even_if_they_reach_it()
    {
        var ok = Guid.NewGuid();
        var no = Guid.NewGuid();
        var sub = new UnfilteredSubgraph([Doc(ok, "internal"), Doc(no, "restricted")], [], false);

        var view = GraphViewResponse.Seal(sub, InternalOnly());

        view.Nodes.Should().ContainSingle().Which.DocumentId.Should().Be(ok);
    }

    // 🔴 IADR-0242 決定 6: **辺は両端点が許可されたときのみ可視。**
    // 片端が濾されたら、その辺は件数にも現れない。
    [Fact]
    public void Seal_drops_an_edge_whose_far_endpoint_is_not_visible()
    {
        var ok = Guid.NewGuid();
        var no = Guid.NewGuid();
        var edge = Edge.Create(ok, no, Guid.NewGuid(), false, EdgeProvenance.Auto);
        var sub = new UnfilteredSubgraph([Doc(ok, "internal"), Doc(no, "restricted")], [edge], false);

        var view = GraphViewResponse.Seal(sub, InternalOnly());

        view.Nodes.Should().ContainSingle();
        view.Edges.Should().BeEmpty("片端が見えない辺は、件数としても現れてはならない");
    }

    // 両端が許可されていれば辺は返る（否定形テストが空振りしていないことの陽性対照）。
    [Fact]
    public void Seal_keeps_an_edge_when_both_endpoints_are_visible()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var edge = Edge.Create(a, b, Guid.NewGuid(), false, EdgeProvenance.User);
        var sub = new UnfilteredSubgraph([Doc(a, "internal"), Doc(b, "internal")], [edge], false);

        var view = GraphViewResponse.Seal(sub, InternalOnly());

        view.Nodes.Should().HaveCount(2);
        view.Edges.Should().ContainSingle().Which.Provenance.Should().Be(EdgeProvenance.User);
    }

    // ADR-0034 決定 4: 打ち切りフラグはそのまま伝わる（権限外を数えないことは #909 側で固定する）。
    [Fact]
    public void Seal_preserves_the_truncated_flag()
    {
        var sub = new UnfilteredSubgraph([], [], true);

        GraphViewResponse.Seal(sub, InternalOnly()).Truncated.Should().BeTrue();
    }

    // IADR-0242 決定 11: ストアの空入力が例外にならない（探索の初手・終端で必ず通る経路）。
    [Fact]
    public async Task Store_returns_empty_for_empty_inputs()
    {
        await using var db = new GraphDbContext(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"Store_{Guid.NewGuid()}").Options);
        var store = new EfGraphStore(db);

        (await store.LoadNodesAsync([], TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.LoadIncidentEdgesAsync([], TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.FindNodeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ADR-0034 決定 4 / IADR-0242 決定 4: **接続辺は決定的順序（CreatedAt, Id）で返る。**
    //
    // 🔴 **挿入順とソート順を意図的にずらす。** 一致させたフィクスチャでは、順序保証を外しても
    // 結果が変わらず（InMemory は挿入順を返す）、変異試験が等価変異になって**何も測れない**。
    // 実際 T-12（打ち切りの決定性）は OrderBy を外しても緑のままだった。
    [Fact]
    public async Task Store_returns_incident_edges_in_deterministic_order()
    {
        await using var db = new GraphDbContext(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"Order_{Guid.NewGuid()}").Options);

        var me = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        db.Documents.Add(GraphDocument.Create(me, "me",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));

        // 先に作った辺ほど CreatedAt が古い。
        var older = Edge.Create(me, Guid.NewGuid(), typeId, false, EdgeProvenance.Auto);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var newer = Edge.Create(me, Guid.NewGuid(), typeId, false, EdgeProvenance.Auto);

        // **挿入は新しい方から**（＝挿入順とソート順が逆になる）。
        db.Edges.Add(newer);
        db.Edges.Add(older);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // フィクスチャが判別可能であることを先に固定する（そうでないとこのテストは空振りする）。
        older.CreatedAt.Should().BeBefore(newer.CreatedAt, "遅延が効かないと順序差が作れない");

        var node = await db.Documents.AsNoTracking()
            .FirstAsync(d => d.DocumentId == me, TestContext.Current.CancellationToken);
        var authorized = AuthorizedNode.Authorize(node, InternalOnly());

        var edges = await new EfGraphStore(db)
            .LoadIncidentEdgesAsync([authorized!], TestContext.Current.CancellationToken);

        edges.Select(e => e.Id).Should().Equal([older.Id, newer.Id],
            "CreatedAt の昇順で返らなければ、打ち切りで落ちる辺が実行ごとに変わる");
    }

    // FR-17: 接続辺は**双方向**に引く（バックリンクが行を増やさずに返るための前提）。
    [Fact]
    public async Task Store_loads_incident_edges_in_both_directions()
    {
        await using var db = new GraphDbContext(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"Store_{Guid.NewGuid()}").Options);

        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        db.EdgeTypes.Add(EdgeType.Create("cites", EdgeTypeLayer.Core, false));
        db.Documents.Add(GraphDocument.Create(me, "me",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, null, DateTimeOffset.UtcNow));
        db.Edges.Add(Edge.Create(me, other, typeId, false, EdgeProvenance.Auto));   // 自分が source
        db.Edges.Add(Edge.Create(other, me, typeId, false, EdgeProvenance.Auto));   // 自分が target（被参照）
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var node = await db.Documents.AsNoTracking()
            .FirstAsync(d => d.DocumentId == me, TestContext.Current.CancellationToken);
        var authorized = AuthorizedNode.Authorize(node, InternalOnly());
        authorized.Should().NotBeNull();

        var edges = await new EfGraphStore(db)
            .LoadIncidentEdgesAsync([authorized!], TestContext.Current.CancellationToken);

        edges.Should().HaveCount(2, "被参照（バックリンク）も同じ 1 クエリで返る必要がある");
    }
}
