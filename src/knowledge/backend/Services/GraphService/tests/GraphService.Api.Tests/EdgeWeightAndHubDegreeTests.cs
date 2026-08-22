using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-04, FR-17, UC-10, ADR-0035 決定 2 (#947a): 辺の型の重みとハブ文書の次数上限。
//
// **重みはまだ誰も使わない**（使うのは二段検索の段）。ここで固定するのは値そのものと、
// 改名で動かないことである。
//
// 🔴 **次数上限の意味は「展開の中継点にしない」であって「結果から除く」ではない。**
// 同じコードで両方書けるが、意味は正反対である。T-05 と T-06 を対で置いて区別する。
public class EdgeWeightAndHubDegreeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EdgeWeightAndHubDegreeTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument Node(Guid id, string name)
        => GraphDocument.Create(id, name,
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null, DateTimeOffset.UtcNow);

    // ── 重み ───────────────────────────────────────────────

    // T-01: 初期値集合の重みが仕様どおり。**ADR が名指しした 2 つ**を特に固定する。
    [Fact]
    public async Task Seeded_edge_types_carry_the_specified_weights()
    {
        var expected = new Dictionary<string, double>
        {
            ["supersedes"] = 1.0,   // ADR: 最新版へ強く誘導する
            ["derived-from"] = 0.8,
            ["cites"] = 0.7,
            ["embeds"] = 0.7,
            ["related"] = 0.3,      // ADR: 弱く扱う
            ["implements"] = 0.5,   // リテラルで測る（定数と突き合わせるとトートロジーになる）
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<GraphService.Api.Foundation.Persistence.GraphDbContext>();
        var types = await db.EdgeTypes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

        types.Should().NotBeEmpty("seed が入っていないと本テストは空振りする");
        foreach (var (name, weight) in expected)
        {
            var t = types.SingleOrDefault(x => x.Name == name);
            t.Should().NotBeNull($"初期値集合に {name} が要る");
            t!.Weight.Should().Be(weight, $"{name} の重み");
        }

        // 🔴 装置の検出力: **supersedes と related に差が付いている**こと。
        // 全部同じ値なら「重みを持たせた」ことに意味が無い。
        types.Single(x => x.Name == "supersedes").Weight
            .Should().BeGreaterThan(types.Single(x => x.Name == "related").Weight,
                "ADR-0035 決定 2 は supersedes を強く・related を弱くと名指ししている");
    }

    // T-02: API で追加した型は既定値。
    [Fact]
    public async Task A_newly_created_type_gets_the_default_weight()
    {
        var name = $"w-{Guid.NewGuid():N}";
        var res = await _factory.CreateClient().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest(name, EdgeTypeLayer.Core, false),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<GraphService.Api.Foundation.Persistence.GraphDbContext>();
        var created = await db.EdgeTypes.AsNoTracking()
            .SingleAsync(t => t.Name == name, TestContext.Current.CancellationToken);

        // 🔴 **リテラルで測る。** `EdgeType.DefaultWeight` と突き合わせると自己参照になり、
        // 定数を変えたときに期待値も一緒に動いて**何も検出しない**（変異 M-5 で実測した）。
        created.Weight.Should().Be(0.5, "新規追加の既定は中庸（0.5）である");

        // **`related`（0.3）より強い。** 未知の型を既定型より弱く扱う根拠が無い。
        // ここも「以上」ではなく**より大きい**で測る —— 0.3 に落とす変異を通さないため。
        created.Weight.Should().BeGreaterThan(0.3,
            "既定を related と同じにすると、未知の型が既定型より弱くならない保証が消える");
    }

    // T-03: 値域。範囲外は**丸めずに拒む**（黙って丸めると設定値と実効値がずれる）。
    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void A_weight_outside_the_range_is_refused(double weight)
    {
        var act = () => EdgeType.Create("x", EdgeTypeLayer.Core, false, weight: weight);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // T-04: 改名しても重みは動かない（改名は表示名の変更であって意味の変更ではない）。
    [Fact]
    public void Renaming_does_not_change_the_weight()
    {
        var t = EdgeType.Create("a", EdgeTypeLayer.Core, false, weight: 0.9);
        t.Rename("b");
        t.Weight.Should().Be(0.9);
    }

    // ── ハブ文書の次数上限 ───────────────────────────────────

    // 起点 → ハブ → 向こう側、という 3 段のグラフを作る。
    // ハブの次数を `hubExtraEdges` 本の余分な辺で膨らませる。
    private async Task<(Guid Origin, Guid Hub, Guid Beyond)> SeedHubAsync(int hubExtraEdges)
    {
        var origin = Guid.NewGuid();
        var hub = Guid.NewGuid();
        var beyond = Guid.NewGuid();
        var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);

        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(origin, "origin"));
            db.Documents.Add(Node(hub, "hub"));
            db.Documents.Add(Node(beyond, "beyond"));
            db.Edges.Add(Edge.Create(origin, hub, type.Id, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(hub, beyond, type.Id, false, EdgeProvenance.Auto));

            // ハブの次数を膨らませる。相手側の文書は作らない —— 次数の計数は
            // **辺の本数**であって、相手が引けるかとは独立である。
            for (var i = 0; i < hubExtraEdges; i++)
                db.Edges.Add(Edge.Create(hub, Guid.NewGuid(), type.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });
        return (origin, hub, beyond);
    }

    // 🔴 応答型（`GraphViewResponse`）は **private ctor の型ゲート**なので逆直列化できない
    // （IADR-0242 決定 2。`Seal` 以外から作れないことが未フィルタ結果の流出を防いでいる）。
    // **テスト側の受け皿を別に持つ** —— 既存の GraphTraversalTests と同じ作法である。
    private sealed record NodeDto(Guid DocumentId, string Title);
    private sealed record EdgeDto(Guid Id, Guid SourceDocumentId, Guid TargetDocumentId);
    private sealed record View(List<NodeDto> Nodes, List<EdgeDto> Edges, bool Truncated);

    private async Task<View> NeighborsAsync(Guid origin)
    {
        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{origin}/neighbors?hops=2", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = await res.Content.ReadFromJsonAsync<View>(TestContext.Current.CancellationToken);
        v.Should().NotBeNull();
        return v!;
    }

    // 🔴 T-05: ハブ文書は**結果に現れる**。除かれない。
    //
    // 除いてしまうと、利用者から見えるはずの関係（「この文書はハブを参照している」）が消える。
    [Fact]
    public async Task A_hub_document_still_appears_in_the_result()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (origin, hub, _) = await SeedHubAsync(GraphTraversal.MaxHubDegree + 5);

        var view = await NeighborsAsync(origin);

        view.Nodes.Select(n => n.DocumentId).Should().Contain(hub,
            "「中継点にしない」は「結果から除く」ではない。"
            + "除くと、利用者から見えるはずの関係そのものが消える");
    }

    // 🔴 T-06: そのハブからは**展開されない**（向こう側が現れない）。
    [Fact]
    public async Task Nothing_is_expanded_through_a_hub_document()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (origin, _, beyond) = await SeedHubAsync(GraphTraversal.MaxHubDegree + 5);

        var view = await NeighborsAsync(origin);

        view.Nodes.Select(n => n.DocumentId).Should().NotContain(beyond,
            "ハブ経由で無関係な文書が芋づる式に湧くのを抑えるのが上限の狙いである");
    }

    // 🔴 T-07 陽性対照（T-06 の対）: 上限以下の文書からは展開される。
    //
    // **T-06 だけでは「何も展開しない実装」を通してしまう。**
    [Fact]
    public async Task An_ordinary_document_is_still_expanded_through()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var (origin, _, beyond) = await SeedHubAsync(hubExtraEdges: 0);

        var view = await NeighborsAsync(origin);

        view.Nodes.Select(n => n.DocumentId).Should().Contain(beyond,
            "次数が上限以下なら従来どおり中継点になる");
    }

    // 🔴 T-08: 次数の計数は**権限判定と独立**。
    //
    // 可視の辺だけを数えると、**同じ文書が利用者によってハブになったりならなかったりする**。
    // ハブ判定はグラフの構造上の性質であって、利用者の権限の関数ではない。
    //
    // ここでは「相手側の文書レコードが存在しない辺」で次数を膨らませている（＝呼び出し主体には
    // 決して見えない辺）。それでもハブと判定されることを固定する。
    [Fact]
    public async Task Degree_counts_edges_the_caller_cannot_see()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        // SeedHubAsync の余分な辺は相手側の文書を作っていない＝利用者からは辿れない。
        var (origin, _, beyond) = await SeedHubAsync(GraphTraversal.MaxHubDegree + 5);

        var view = await NeighborsAsync(origin);

        view.Nodes.Select(n => n.DocumentId).Should().NotContain(beyond,
            "見えない辺も次数に数える。数えないと上限が利用者ごとに変わる");
    }
}
