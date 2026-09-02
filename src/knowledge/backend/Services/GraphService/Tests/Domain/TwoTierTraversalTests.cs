using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Domain;

// FR-17, UC-10, ADR-0049 (#980): 二段の上限・総数・間引き基準。
//
// **覆うのは FR-17 である。** 起草時（#980）は「これらを消費する画面は未実装でテスト仕様書も
// 無い」ことを画面 ID を参照しない理由に挙げていたが、**その前提は #917 で変わった**
// （SC-18 は実装済みで docs/tests/ にテスト仕様書もある）。それでも本クラスは画面 ID を
// 参照しない —— ここが試験しているのは**探索の性質**であって画面の受け入れ基準ではなく、
// 画面側の写像は EdgeTypeFilterTests / NodeScopeFlagTests と SPA 側のテストが担う。
//
// 🔴 **ADR-0049 が改めたのは ADR-0034 の 1 行だけである** ——「内部の探索も同じ上限で打ち切る」。
// **決定 1 の具体化（不許可ノードはその場で打ち切り、次ホップへ進まない）は改まっていない。**
// むしろ本改定の前提である（許可済みだけを数える限り「中間結果に不許可文書が載らない」という
// 理由は損なわれない、というのが改定の根拠だから）。
public class TwoTierTraversalTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TwoTierTraversalTests(TestWebApplicationFactory factory) => _factory = factory;

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument Node(Guid id, string name, string conf = "internal",
        DateTimeOffset? updatedAt = null)
        => GraphDocument.Create(id, name,
            new Dictionary<string, string> { ["confidentiality"] = conf },
            null, updatedAt ?? DateTimeOffset.UtcNow);

    // 応答型は private ctor の型ゲート（IADR-0242 決定 2）なので逆直列化できない。テスト側の受け皿を持つ。
    private sealed record NodeDto(Guid DocumentId, string Title);
    private sealed record EdgeDto(Guid Id, Guid SourceDocumentId, Guid TargetDocumentId);
    private sealed record View(
        List<NodeDto> Nodes, List<EdgeDto> Edges, bool Truncated,
        int TotalNodes, int TotalEdges, bool TotalIsLowerBound);

    private async Task<View> GetAsync(Guid origin, string? by = null, int hops = 2)
    {
        var url = $"/graph/{origin}/neighbors?hops={hops}" + (by is null ? "" : $"&by={by}");
        var res = await _factory.CreateClient().GetAsync(url, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = await res.Content.ReadFromJsonAsync<View>(TestContext.Current.CancellationToken);
        v.Should().NotBeNull();
        return v!;
    }

    // 星型（起点 → n 個の葉）を作る。葉ごとに機密区分と更新日を指定できる。
    private async Task<Guid> SeedStarAsync(int visible, int hidden = 0,
        Func<int, DateTimeOffset>? updatedAt = null)
    {
        var origin = Guid.NewGuid();
        var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(origin, "origin"));
            for (var i = 0; i < visible; i++)
            {
                var leaf = Guid.NewGuid();
                db.Documents.Add(Node(leaf, $"v{i}", "internal", updatedAt?.Invoke(i)));
                db.Edges.Add(Edge.Create(origin, leaf, type.Id, false, EdgeProvenance.Auto));
            }
            for (var i = 0; i < hidden; i++)
            {
                var leaf = Guid.NewGuid();
                db.Documents.Add(Node(leaf, $"h{i}", "restricted"));
                db.Edges.Add(Edge.Create(origin, leaf, type.Id, false, EdgeProvenance.Auto));
            }
            return Task.CompletedTask;
        });
        return origin;
    }

    // 🔴 T-01: 表示は 200 件で頭打ちだが、**総数はそれを超える**。
    //
    // **総数は「返した件数」ではなく「間引く前の全体」である。** 両者が一致する小さなデータでは
    // 取り違えを検出できないので、**わざと食い違う母集合**（250 件）で測る。
    [Fact]
    public async Task The_total_exceeds_the_displayed_count()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: 250);

        var view = await GetAsync(origin);

        view.Nodes.Should().HaveCount(GraphTraversal.MaxNodes, "表示は 200 件で頭打ち");
        view.TotalNodes.Should().Be(251, "起点 1 + 可視の葉 250。**返した件数と一致してはならない**");
        view.TotalNodes.Should().BeGreaterThan(view.Nodes.Count);
        view.Truncated.Should().BeTrue();
    }

    // 🔴 T-02: **権限外は総数に入らない**（否定形）。
    //
    // ADR-0049 決定 1「数えてよいのは ABAC 判定を通過した品目だけ」。この条件下でのみ
    // ADR-0034 の「中間結果に不許可文書が載らない」という理由が保たれる。
    [Fact]
    public async Task Unauthorized_nodes_are_not_counted_in_the_total()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: 10, hidden: 40);

        var view = await GetAsync(origin);

        view.TotalNodes.Should().Be(11,
            "起点 1 + 可視 10。**権限外 40 件は 1 件も数えない**");
        view.TotalNodes.Should().NotBe(51, "権限外を数えると 51 になる");
    }

    // T-03 陽性対照（T-02 の対）: 可視ノードは総数に入る。
    //
    // **これが無いと「常に 1 を返す実装」が T-02 を通る。**
    [Fact]
    public async Task Authorized_nodes_are_counted_in_the_total()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: 10, hidden: 40);

        var view = await GetAsync(origin);

        view.TotalNodes.Should().BeGreaterThan(1, "可視ノードが 1 件も数えられていない");
        view.Nodes.Select(n => n.Title).Where(t => t.StartsWith('v')).Should().HaveCount(10);
    }

    // 🔴 T-05 陰性対照: **上限に届かないときは二段目へ行かない。**
    //
    // 「常に二段走査する実装」は T-01 を通してしまう。**届かないときに `Truncated` が立たず、
    // 総数と表示件数が一致すること**で、二段目に入っていないことを測る。
    [Fact]
    public async Task A_small_graph_does_not_enter_the_second_tier()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: 5);

        var view = await GetAsync(origin);

        view.Truncated.Should().BeFalse("上限に届いていないので打ち切っていない");
        view.TotalIsLowerBound.Should().BeFalse("算出用上限にも達していない");
        view.TotalNodes.Should().Be(view.Nodes.Count, "選抜が起きていないので総数＝表示件数");
        view.TotalNodes.Should().Be(6);
    }

    // 🔴 T-04 陽性対照: **算出用上限（2,000）に達すると下限フラグが立つ。**
    //
    // **この対が無いと「常に false を返す実装」が下の否定形を通る。** 実際、初回の変異試験で
    // M-3（下限フラグを立てない）が**落ちなかった** —— true になる経路を一度も作っていなかった
    // ためである。**「別クラスで測る」と書いて、そのクラスを作っていなかった。**
    [Fact]
    public async Task The_lower_bound_flag_is_set_when_the_counting_limit_is_reached()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: GraphTraversal.CountingMaxNodes + 10);

        var view = await GetAsync(origin);

        view.TotalIsLowerBound.Should().BeTrue(
            "算出用上限に達したので総数は「以上」の意味である（ADR-0049 決定 3）");
        view.TotalNodes.Should().Be(GraphTraversal.CountingMaxNodes,
            "正確な数を出すために探索を伸ばさない");
        view.Nodes.Should().HaveCount(GraphTraversal.MaxNodes, "表示は 200 件のまま");
    }

    // T-05: 算出用上限に**達していない**ので下限フラグは立たない（上の対）。
    [Fact]
    public async Task The_lower_bound_flag_is_not_set_below_the_counting_limit()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = await SeedStarAsync(visible: 250);

        var view = await GetAsync(origin);

        view.TotalIsLowerBound.Should().BeFalse(
            "候補 251 件は算出用上限 2,000 に遠く届かない");
    }

    // 🔴 T-06: `updated` 指定で更新日の新しい順に選ばれる。
    //
    // 到達順とわざと逆にした母集合で測る —— **順番が同じフィクスチャでは基準が効いたか判らない。**
    [Fact]
    public async Task Thinning_by_updated_picks_the_newest()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // i が大きいほど**新しい**。挿入順（到達順）は i の昇順なので、両者は逆向きである。
        var origin = await SeedStarAsync(visible: 250, updatedAt: i => baseTime.AddDays(i));

        var view = await GetAsync(origin, by: GraphThinning.Updated);

        view.Nodes.Should().HaveCount(GraphTraversal.MaxNodes);
        // 新しい 199 件（v249..v51）＋ 起点。**到達順なら v0..v198 が残るはずである。**
        var titles = view.Nodes.Select(n => n.Title).ToHashSet();
        titles.Should().Contain("v249", "最も新しい葉が残る");
        titles.Should().NotContain("v0", "最も古い葉は落ちる（到達順なら残っていた）");
    }

    // 🔴 T-09: 既定（未指定）の並びは**現行と同じ**＝到達順。
    //
    // T-06 の対。**基準を無視して常に到達順にする実装**は T-06 で落ちるが、
    // **常に更新日順にする実装**は本テストで落ちる。
    [Fact]
    public async Task The_default_thinning_keeps_the_discovery_order()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var origin = await SeedStarAsync(visible: 250, updatedAt: i => baseTime.AddDays(i));

        var view = await GetAsync(origin);   // by を指定しない

        var titles = view.Nodes.Select(n => n.Title).ToHashSet();
        titles.Should().Contain("v0", "到達順なら最初の葉が残る");
        titles.Should().NotContain("v249", "更新日順ならこれが残るが、既定は到達順である");
    }

    // T-10: 未知の基準は既定へ縮退する（例外にしない）。
    [Fact]
    public async Task An_unknown_thinning_falls_back_to_the_default()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var origin = await SeedStarAsync(visible: 250, updatedAt: i => baseTime.AddDays(i));

        var view = await GetAsync(origin, by: "no-such-criterion");

        view.Nodes.Should().HaveCount(GraphTraversal.MaxNodes, "例外にせず結果を返す");
        view.Nodes.Select(n => n.Title).Should().Contain("v0", "既定（到達順）へ縮退する");
    }

    // 🔴 T-08: 属性を持たない文書は総数に入らない（deny-by-default の帰結）。
    //
    // **バグではない。** ADR-0034 決定 2 が「権限外と未同期を区別しない」と定めており、
    // `AbacNodeFilter` は**属性キーの欠落を不一致に倒す**。
    [Fact]
    public async Task Documents_without_attributes_are_not_counted()
    {
        _factory.ScopeProvider = _ => InternalOnly();
        var origin = Guid.NewGuid();
        var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        var synced = Guid.NewGuid();
        var notSynced = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(origin, "origin"));
            db.Documents.Add(Node(synced, "synced"));
            // 属性を持たない（複製未到達）。
            db.Documents.Add(GraphDocument.Create(notSynced, "not-yet-synced",
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow));
            db.Edges.Add(Edge.Create(origin, synced, type.Id, false, EdgeProvenance.Auto));
            db.Edges.Add(Edge.Create(origin, notSynced, type.Id, false, EdgeProvenance.Auto));
            return Task.CompletedTask;
        });

        var view = await GetAsync(origin);

        view.TotalNodes.Should().Be(2, "起点 + 同期済み 1 件。**未同期は数えない**");
        view.Nodes.Select(n => n.Title).Should().NotContain("not-yet-synced");
    }

    // T-11: ホップ超過は従前どおり 400。**認可より前に検証する順序も不変。**
    [Fact]
    public async Task Hops_out_of_range_is_still_rejected_before_authorization()
    {
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], false);

        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{Guid.NewGuid()}/neighbors?hops=4", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "スコープが空（deny）でも 400 が返る＝検証が認可より前にある");
    }
}
