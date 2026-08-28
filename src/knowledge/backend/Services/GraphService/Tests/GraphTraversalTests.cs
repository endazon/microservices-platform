using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-17, UC-10, ADR-0034 決定 1・2・3・4, IADR-0242: 多ホップ探索（#909）。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く。** フィクスチャが壊れて「そもそも到達し得ない」
// 状態でも否定形は緑になるため、陽性対照が無い否定形テストは**何も測っていない**。
public class GraphTraversalTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public GraphTraversalTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument Node(Guid id, string name, string confidentiality)
        => GraphDocument.Create(id, name,
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            null, DateTimeOffset.UtcNow);

    private sealed record View(List<GraphNodeDto> Nodes, List<GraphEdgeDto> Edges, bool Truncated);

    private async Task<View> GetAsync(Guid origin, int? hops = null)
    {
        var url = $"/graph/{origin}/neighbors" + (hops is null ? "" : $"?hops={hops}");
        var res = await _factory.CreateClient().GetAsync(url, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = await res.Content.ReadFromJsonAsync<View>(TestContext.Current.CancellationToken);
        v.Should().NotBeNull();
        return v!;
    }

    // A→X→B の直線を作る。X の機密区分だけを引数で変える。
    private async Task<(Guid A, Guid X, Guid B)> SeedChainAsync(string middleConfidentiality)
    {
        var a = Guid.NewGuid();
        var x = Guid.NewGuid();
        var b = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create($"t-{typeId:N}", EdgeTypeLayer.Core, false));
            db.Documents.Add(Node(a, "A", "internal"));
            db.Documents.Add(Node(x, "X", middleConfidentiality));
            db.Documents.Add(Node(b, "B", "internal"));
            // **A→B の直辺は張らない。** B へ至る許可経路は X を通る 1 本だけにする。
            db.Edges.Add(Edge.Create(a, x, typeId, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(x, b, typeId, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return (a, x, b);
    }

    // 🔴 T-08 否定形: 非許可ノードは「橋」になれない。
    [Fact]
    public async Task Unauthorized_middle_node_cannot_bridge_to_the_far_node()
    {
        var (a, x, b) = await SeedChainAsync("restricted");   // X が権限外
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(a, hops: 2);

        var ids = view.Nodes.Select(n => n.DocumentId).ToList();
        ids.Should().Contain(a);
        ids.Should().NotContain(x, "権限外ノードは現れてはならない");
        ids.Should().NotContain(b,
            "X を経由しないと B へ届かない。B が現れたら判定が展開の後ろに落ちている（ADR-0034 決定 1 違反）");
        view.Edges.Should().BeEmpty("権限外ノードに接続する辺は件数としても現れてはならない");
    }

    // T-08P 陽性対照: 同じトポロジで X を許可にすると B は現れる。
    // これが無いと、フィクスチャの不備（辺の向き違い等）で B に到達し得ない状態でも T-08 が緑になる。
    [Fact]
    public async Task Positive_control_the_far_node_appears_when_the_middle_is_authorized()
    {
        var (a, x, b) = await SeedChainAsync("internal");     // X も許可
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(a, hops: 2);

        var ids = view.Nodes.Select(n => n.DocumentId).ToList();
        ids.Should().Contain([a, x, b]);
        view.Edges.Should().HaveCount(2);
    }

    // 深さ 1 では B へ届かない（hops が実際に効いていることの対照）。
    [Fact]
    public async Task One_hop_does_not_reach_the_second_ring()
    {
        var (a, x, b) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(a, hops: 1);

        view.Nodes.Select(n => n.DocumentId).Should().Contain([a, x]).And.NotContain(b);
    }

    // 星形（中心 ＋ 許可 n 件 ＋ 権限外 m 件）を作る。
    private async Task<Guid> SeedStarAsync(int authorized, int forbidden)
    {
        var center = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create($"t-{typeId:N}", EdgeTypeLayer.Core, false));
            db.Documents.Add(Node(center, "C", "internal"));
            for (var i = 0; i < authorized; i++)
            {
                var id = Guid.NewGuid();
                db.Documents.Add(Node(id, $"ok{i}", "internal"));
                db.Edges.Add(Edge.Create(center, id, typeId, false, EdgeProvenance.Auto));
            }
            for (var i = 0; i < forbidden; i++)
            {
                var id = Guid.NewGuid();
                db.Documents.Add(Node(id, $"no{i}", "restricted"));
                db.Edges.Add(Edge.Create(center, id, typeId, false, EdgeProvenance.Auto));
            }
            return Task.CompletedTask;
        });
        return center;
    }

    // 🔴 T-09 否定形: 上限の計数に権限外を含めない。
    [Fact]
    public async Task Display_cap_does_not_count_unauthorized_nodes()
    {
        // 許可 150 ＋ 権限外 100。判定前に数えると 251 > 200 で打ち切りが立つ。
        var center = await SeedStarAsync(authorized: 150, forbidden: 100);
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(center, hops: 1);

        view.Nodes.Should().HaveCount(151, "中心 1 ＋ 許可 150。権限外 100 は数に入らない");
        view.Truncated.Should().BeFalse(
            "権限外を数えると 251 > 200 で打ち切りが立つ。立ったら計数が判定の前にある");
    }

    // T-09P 陽性対照: 上限そのものは働く。
    [Fact]
    public async Task Positive_control_the_cap_actually_triggers_when_authorized_nodes_exceed_it()
    {
        var center = await SeedStarAsync(authorized: 250, forbidden: 0);
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(center, hops: 1);

        view.Truncated.Should().BeTrue();
        view.Nodes.Should().HaveCount(GraphTraversal.MaxNodes);
    }

    // 🔴 T-10: 深さ上限の超過は 400（黙って切り詰めない）。
    [Fact]
    public async Task Hops_above_the_maximum_is_rejected_not_clamped()
    {
        var (a, _, _) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{a}/neighbors?hops=4", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "切り詰めると利用者は欠けた結果を「上限まで見た」と誤解する");
    }

    [Fact]
    public async Task Hops_below_one_is_rejected()
    {
        var (a, _, _) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{a}/neighbors?hops=0", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-10P 陽性対照: 上限ちょうどは通る（「全部 400」で緑になっていないこと）。
    [Fact]
    public async Task Positive_control_hops_at_the_maximum_succeeds()
    {
        var (a, _, _) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => InternalOnly();

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{a}/neighbors?hops={GraphTraversal.MaxHops}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 既定値は 2（省略時に深さ 2 相当の結果になる）。
    [Fact]
    public async Task Default_hops_is_two()
    {
        var (a, x, b) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(a);   // hops 省略

        view.Nodes.Select(n => n.DocumentId).Should().Contain([a, x, b]);
    }

    // T-11: バックリンク（被参照）が行を増やさずに返る。
    [Fact]
    public async Task Backlinks_are_returned_without_extra_rows()
    {
        var me = Guid.NewGuid();
        var citing = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(EdgeType.Create($"t-{typeId:N}", EdgeTypeLayer.Core, false));
            db.Documents.Add(Node(me, "me", "internal"));
            db.Documents.Add(Node(citing, "citing", "internal"));
            // citing → me の 1 行だけ（me からの順方向の辺は無い）。
            db.Edges.Add(Edge.Create(citing, me, typeId, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        _factory.ScopeProvider = _ => InternalOnly();

        var view = await GetAsync(me, hops: 1);

        view.Nodes.Select(n => n.DocumentId).Should().Contain(citing,
            "被参照は逆引きで辿れる必要がある");
        view.Edges.Should().ContainSingle();
    }

    // T-12: 打ち切りが決定的（同一入力で 2 回実行して同一応答）。
    [Fact]
    public async Task Truncation_is_deterministic()
    {
        var center = await SeedStarAsync(authorized: 250, forbidden: 0);
        _factory.ScopeProvider = _ => InternalOnly();

        var first = await GetAsync(center, hops: 1);
        var second = await GetAsync(center, hops: 1);

        first.Truncated.Should().BeTrue();
        second.Nodes.Select(n => n.DocumentId)
            .Should().Equal(first.Nodes.Select(n => n.DocumentId),
                "打ち切りが非決定だとテストが flake し、利用者から見て見えたり見えなかったりする");
    }

    // 🔴 ADR-0034 決定 2: **hops の検証は文書の可視性を漏らしてはならない。**
    //
    // 認可を先に置くと、権限外・不存在は 404 / 可視だけ 400 となり、hops=4 を投げるだけで
    // 文書の存在が判る。検証を先に置く現在の順序ならどちらも 400 で区別できない。
    // このテストは「順序を入れ替えたら落ちる」ことで順序を固定する。
    [Fact]
    public async Task Hops_validation_does_not_leak_document_visibility()
    {
        var (visible, _, _) = await SeedChainAsync("internal");
        var nonexistent = Guid.NewGuid();
        _factory.ScopeProvider = _ => InternalOnly();
        var client = _factory.CreateClient();

        var a = await client.GetAsync($"/graph/{visible}/neighbors?hops=4", TestContext.Current.CancellationToken);
        var b = await client.GetAsync($"/graph/{nonexistent}/neighbors?hops=4", TestContext.Current.CancellationToken);

        a.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        b.StatusCode.Should().Be(HttpStatusCode.BadRequest, "不存在でも hops の検証が先に効く");
        (await a.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(await b.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                "応答本文に差があると、そこから文書の存在が読める");
    }

    // FR-05: スコープが無ければ探索そのものが 404（起点も見せない）。
    [Fact]
    public async Task Not_granted_scope_yields_404_for_traversal_too()
    {
        var (a, _, _) = await SeedChainAsync("internal");
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], false);

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{a}/neighbors", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
