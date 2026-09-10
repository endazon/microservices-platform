using AwesomeAssertions;
using GraphService.Domain.Clustering;

namespace GraphService.Tests.Domain.Clustering;

// FR-17, SC-10, ADR-0035 決定 6, ADR-0083 決定 3 条件 2, [[IADR-0425]] 決定 3 (#1363):
// 日次の検出結果と、永続化済みのクラスタの対応づけ。
//
// なぜ要るか —— 「クラスタ構成が変わった**後に**再生成されていない」を判定するには、
// 昨日のクラスタと今日のクラスタが**同じものか**が要る。
public sealed class ClusterReconcilerTests
{
    // FR-17 (T-8): 構成が同一なら「変わっていない」に入る。
    // 🔴 **ここが要点。** 日次バッチは毎日走るので、同一でも「変わった」と判定する実装だと
    // 全クラスタが恒久的に未要約になり、指標が読めなくなる。
    [Fact]
    public void 構成が同一なら変わっていないクラスタとして扱う()
    {
        var existing = new[] { Existing(1, 1, 2, 3) };

        var result = ClusterReconciler.Reconcile([Members(1, 2, 3)], existing);

        result.Unchanged.Should().ContainSingle().Which.ClusterId.Should().Be(ClusterId(1));
        result.Changed.Should().BeEmpty();
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
    }

    // FR-17 (T-9): 過半を共有していれば**同じクラスタ**であり、構成が変わったものとして扱う。
    // クラスタ ID が保たれるので、既に生成済みの要約は引き継がれる（そして「未要約」に数えられる）。
    [Fact]
    public void 過半を共有する検出結果は同じクラスタの構成変更として扱う()
    {
        var existing = new[] { Existing(1, 1, 2, 3, 4) };

        // 4 件中 3 件が残り 1 件増える → Jaccard = 3/5 = 0.6。
        var result = ClusterReconciler.Reconcile([Members(1, 2, 3, 5)], existing);

        result.Changed.Should().ContainSingle().Which.ClusterId.Should().Be(ClusterId(1));
        result.Unchanged.Should().BeEmpty();
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
    }

    // 🔴 FR-17 (T-10): **陰性対照。** 過半を共有しないものは**別のクラスタ**である。
    // ここを緩めると、まったく別の文書集合へ古い要約が引き継がれる。
    [Fact]
    public void 過半を共有しない検出結果は別のクラスタになる()
    {
        var existing = new[] { Existing(1, 1, 2, 3, 4) };

        // 4 件中 1 件しか共有しない → Jaccard = 1/7。
        var result = ClusterReconciler.Reconcile([Members(1, 5, 6, 7)], existing);

        result.Added.Should().ContainSingle();
        result.Removed.Should().ContainSingle().Which.Should().Be(ClusterId(1));
        result.Changed.Should().BeEmpty();
        result.Unchanged.Should().BeEmpty();
    }

    // FR-17 (T-10b): **境界。** Jaccard がちょうど 0.5 なら同じクラスタである（`>=`）。
    [Fact]
    public void 係数がちょうど半分なら同じクラスタとして扱う()
    {
        var existing = new[] { Existing(1, 1, 2, 3, 4) };

        // 共有 3・和集合 6 → 0.5 ちょうど。
        var result = ClusterReconciler.Reconcile([Members(1, 2, 3, 5, 6)], existing);

        result.Changed.Should().ContainSingle().Which.ClusterId.Should().Be(ClusterId(1));
    }

    // FR-17 (T-10c): 対応がつかない検出結果は新規、既存クラスタは消滅である。
    [Fact]
    public void 対応のつかない検出結果は新規で既存クラスタは消滅する()
    {
        var existing = new[] { Existing(1, 1, 2), Existing(2, 3, 4) };

        var result = ClusterReconciler.Reconcile([Members(5, 6)], existing);

        result.Added.Should().ContainSingle().Which.Should().BeEquivalentTo(Members(5, 6));
        result.Removed.Should().BeEquivalentTo([ClusterId(1), ClusterId(2)]);
    }

    // FR-17 (T-10d): 1 つの検出結果へ 2 つの既存クラスタが競合したら、
    // **係数の高い方が取る**（貪欲・1 対 1）。負けた側は消滅する。
    [Fact]
    public void 競合したら係数の高い既存クラスタが対応を取る()
    {
        var existing = new[]
        {
            Existing(1, 1, 2, 9),       // 共有 2・和集合 4 → 0.5
            Existing(2, 1, 2, 3),       // 共有 3・和集合 3 → 1.0
        };

        var result = ClusterReconciler.Reconcile([Members(1, 2, 3)], existing);

        result.Unchanged.Should().ContainSingle().Which.ClusterId.Should().Be(ClusterId(2));
        result.Removed.Should().BeEquivalentTo([ClusterId(1)]);
    }

    // FR-17 (T-10e): 初回（既存が空）は全部が新規である。
    [Fact]
    public void 既存が無ければすべて新規になる()
    {
        var result = ClusterReconciler.Reconcile([Members(1, 2), Members(3)], []);

        result.Added.Should().HaveCount(2);
        result.Removed.Should().BeEmpty();
    }

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static Guid ClusterId(int n) => Guid.Parse($"00000000-0000-0000-0001-{n:D12}");

    private static List<Guid> Members(params int[] ns) => [.. ns.Select(Id)];

    private static ExistingCluster Existing(int cluster, params int[] members) =>
        new(ClusterId(cluster), Members(members));
}
