using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph.Neighbors;

// FR-17, UC-10, SC-18 (#917): 辺の型フィルタの**サーバ側**適用。
//
// 🔴 **クライアントで打ち切り後に絞ってはならない**（planning#446 / 05_screens §SC-18）——
// 「フィルタ後の上位 200 件」ではなく「上位 200 件のうち一致したもの」になり、利用者が見る範囲が
// 意図せず狭まる。したがって本テストは (a) 絞りが探索そのものに効くこと（型を消すと、その型の辺で
// しか到達できないノードごと消える）と、(b) 総数がフィルタ後の母集合で数え直されることを固定する。
public class EdgeTypeFilterTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EdgeTypeFilterTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument Node(Guid id, string name)
        => GraphDocument.Create(id, name,
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null, DateTimeOffset.UtcNow);

    // 応答型は private ctor の型ゲート（IADR-0242 決定 2）なので逆直列化できない。テスト側の受け皿を持つ。
    private sealed record NodeDto(Guid DocumentId, string Title);
    private sealed record EdgeDto(Guid Id, Guid EdgeTypeId);
    private sealed record View(
        List<NodeDto> Nodes, List<EdgeDto> Edges, bool Truncated, int TotalNodes, int TotalEdges);

    private async Task<View> GetOkAsync(Guid origin, string query)
    {
        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{origin}/neighbors{query}", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = await res.Content.ReadFromJsonAsync<View>(TestContext.Current.CancellationToken);
        v.Should().NotBeNull();
        return v!;
    }

    /// <summary>
    /// origin --cites--> a、origin --related--> b、b --cites--> b2 の 4 ノード。
    /// b2 は cites の辺しか持たないが、**related の辺を経由しないと到達できない**。
    /// </summary>
    private async Task<(Guid Origin, Guid A, Guid B, Guid B2, EdgeType Cites, EdgeType Related)> SeedForkAsync()
    {
        var (origin, a, b, b2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cites = EdgeType.Create($"cites-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        var related = EdgeType.Create($"related-{Guid.NewGuid():N}", EdgeTypeLayer.Core, true);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.AddRange(cites, related);
            db.Documents.AddRange(Node(origin, "origin"), Node(a, "a"), Node(b, "b"), Node(b2, "b2"));
            db.Edges.Add(Edge.Create(origin, a, cites.Id, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(origin, b, related.Id, true, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(b, b2, cites.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return (origin, a, b, b2, cites, related);
    }

    // FR-17, SC-18: 型で絞ると、当該型の辺と「その辺でしか到達できないノード」が探索ごと消える（否定形）。
    //
    // b2 の辺は cites（絞りに一致する型）だが、related を落とすと b に到達しないため b2 も現れない。
    // **打ち切り後のクライアント絞りではこの形にならない**（辺だけ消えてノードが残る）。
    [Fact]
    public async Task Filtering_prunes_traversal_not_just_edges()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var g = await SeedForkAsync();

        var view = await GetOkAsync(g.Origin, $"?types={g.Cites.Id}");

        view.Nodes.Select(n => n.DocumentId).Should().BeEquivalentTo([g.Origin, g.A],
            "related を落とすと b に到達せず、b2 も（辺の型は cites なのに）現れない");
        view.Edges.Should().OnlyContain(e => e.EdgeTypeId == g.Cites.Id);
        view.Edges.Should().HaveCount(1);
    }

    // FR-17, SC-18 陽性対照（上の対）: 絞らなければ全ノード・全辺が現れる。
    //
    // **これが無いと「常に起点だけを返す実装」が上のテストを通る。**
    [Fact]
    public async Task Without_filter_everything_is_reachable()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var g = await SeedForkAsync();

        var view = await GetOkAsync(g.Origin, "");

        view.Nodes.Select(n => n.DocumentId).Should().BeEquivalentTo([g.Origin, g.A, g.B, g.B2]);
        view.Edges.Should().HaveCount(3);
    }

    // FR-17, SC-18: 複数型はカンマ区切りの**和集合**で絞る。
    [Fact]
    public async Task Multiple_types_are_a_union()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var g = await SeedForkAsync();

        var view = await GetOkAsync(g.Origin, $"?types={g.Cites.Id},{g.Related.Id}");

        view.Nodes.Should().HaveCount(4, "両方の型を許せば絞りなしと同じ");
        view.Edges.Should().HaveCount(3);
    }

    // FR-17, SC-18, ADR-0049: 総数は**フィルタ後の母集合**で数え直す。
    //
    // 帯の「全 N 件」は「いま選んでいる型での全数」である。フィルタ前の総数を返すと、
    // 利用者は「絞ったのに件数が減らない」画面を見る。
    [Fact]
    public async Task Totals_are_recomputed_on_the_filtered_population()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = Guid.NewGuid();
        var typeA = EdgeType.Create($"a-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        var typeB = EdgeType.Create($"b-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.AddRange(typeA, typeB);
            db.Documents.Add(Node(origin, "origin"));
            for (var i = 0; i < 150; i++)
            {
                var leaf = Guid.NewGuid();
                db.Documents.Add(Node(leaf, $"a{i}"));
                db.Edges.Add(Edge.Create(origin, leaf, typeA.Id, false, EdgeProvenance.Auto));
            }
            for (var i = 0; i < 100; i++)
            {
                var leaf = Guid.NewGuid();
                db.Documents.Add(Node(leaf, $"b{i}"));
                db.Edges.Add(Edge.Create(origin, leaf, typeB.Id, false, EdgeProvenance.Auto));
            }
            return Task.CompletedTask;
        });

        var unfiltered = await GetOkAsync(origin, "");
        unfiltered.TotalNodes.Should().Be(251, "陽性対照: 絞らなければ 起点 1 + 250");
        unfiltered.Truncated.Should().BeTrue("251 > 表示上限 200");

        var filtered = await GetOkAsync(origin, $"?types={typeA.Id}");
        filtered.TotalNodes.Should().Be(151, "起点 1 + typeA の葉 150。typeB の 100 件は母集合に入らない");
        filtered.TotalEdges.Should().Be(150);
        filtered.Truncated.Should().BeFalse("フィルタ後の母集合は表示上限に収まる");
        filtered.Nodes.Should().HaveCount(151);
    }

    // FR-17, SC-18: 形式不正（GUID として読めない）は 400。**黙って無視しない。**
    [Fact]
    public async Task Malformed_type_id_is_rejected_with_400()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var g = await SeedForkAsync();

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{g.Origin}/neighbors?types=not-a-guid", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("edge_type_filter_invalid");
    }

    // FR-17, SC-18, ADR-0034 決定 2: types の検証は**認可より前**。
    //
    // 🔴 後ろへ置くと、不可視の文書は 404・可視の文書だけが 400 になり、**不正な types を投げるだけで
    // 文書の存在が判る**（hops の検証と同じ理由・同じ並び）。存在しない文書でも 400 が返ることで固定する。
    [Fact]
    public async Task Malformed_types_returns_400_even_for_a_nonexistent_document()
    {
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{Guid.NewGuid()}/neighbors?types=not-a-guid",
                TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "404 を先に返すと、400/404 の打ち分けから存在が漏れる");
    }

    // FR-17, SC-18: 実在しない型 ID（形式は正しい）は**エラーにしない**。単に 1 本も一致しない。
    //
    // 辺の型辞書は認証のみで全利用者へ公開済みの語彙（#962）であり、実在の有無は秘匿対象ではない。
    // 辞書の改廃と共有済み URL が競合しても画面が壊れない形にする。
    [Fact]
    public async Task Unknown_but_wellformed_type_id_matches_nothing()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var g = await SeedForkAsync();

        var view = await GetOkAsync(g.Origin, $"?types={Guid.NewGuid()}");

        view.Nodes.Select(n => n.DocumentId).Should().BeEquivalentTo([g.Origin], "起点だけが残る");
        view.Edges.Should().BeEmpty();
    }
}
