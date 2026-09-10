using AwesomeAssertions;
using GraphService.Domain.Clustering;

namespace GraphService.Tests.Domain.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3, ADR-0083 決定 1, [[IADR-0425]] (#1363):
// Leiden 法によるコミュニティ検出。
//
// 🔴 **本ファイルの中心は T-3 である** —— 「検出されたクラスタは内部で連結している」。
// 計画が Louvain を退けた理由がこれであり（ADR-0035 決定 3「Louvain は**分断されたコミュニティを
// 生じ得る**ため採らない」）、この主張が無いと Louvain との差が試験に現れない。
public sealed class LeidenCommunityDetectorTests
{
    // FR-17 (T-1): 橋 1 本で繋いだ 2 つの塊は、**2 つのクラスタ**に分かれる。
    // これが「コミュニティ検出が働いている」の最小の証拠である。
    [Fact]
    public void 橋一本で繋がれた二つの塊は二つのクラスタに分かれる()
    {
        var left = Clique(1, 2, 3, 4);
        var right = Clique(5, 6, 7, 8);
        var edges = left.Concat(right).Append(Edge(4, 5)).ToList();

        var clusters = LeidenCommunityDetector.Detect(Nodes(1, 2, 3, 4, 5, 6, 7, 8), edges);

        clusters.Should().HaveCount(2);
        clusters.Select(c => c.ToHashSet()).Should().BeEquivalentTo(
            [Nodes(1, 2, 3, 4).ToHashSet(), Nodes(5, 6, 7, 8).ToHashSet()]);
    }

    // FR-17 (T-1b): **陽性対照。** 1 つの塊しかなければ 1 つのクラスタである。
    // これが無いと「常に 2 つに割る実装」でも T-1 は緑になる。
    [Fact]
    public void 単一の塊は一つのクラスタになる()
    {
        var clusters = LeidenCommunityDetector.Detect(Nodes(1, 2, 3, 4), Clique(1, 2, 3, 4));

        clusters.Should().ContainSingle().Which.Should().BeEquivalentTo(Nodes(1, 2, 3, 4));
    }

    // 🔴 FR-17 (T-2): **陰性対照。** 連結していない塊は**決して併合されない**。
    // modularity の増分は「辺のある相手」にしか正にならず、候補も隣接コミュニティに限ってある。
    [Fact]
    public void 連結していない成分は別のクラスタのままである()
    {
        var edges = Clique(1, 2, 3, 4).Concat(Clique(5, 6, 7, 8)).ToList();

        var clusters = LeidenCommunityDetector.Detect(Nodes(1, 2, 3, 4, 5, 6, 7, 8), edges);

        clusters.Should().HaveCount(2);
        clusters.Select(c => c.ToHashSet()).Should().BeEquivalentTo(
            [Nodes(1, 2, 3, 4).ToHashSet(), Nodes(5, 6, 7, 8).ToHashSet()],
            "辺の無い相手へ移れる実装は、無関係な文書を同じクラスタへ入れる");
    }

    // 🔴 FR-17 (T-3): **Leiden の要点。** どのクラスタも**内部で連結している**。
    // Louvain はこの性質を保証しない（計画が退けた理由）。塊を鎖状に繋いだ、
    // 「途中の塊だけが別クラスタへ抜けると分断が起きる」形で測る。
    [Fact]
    public void 検出されたクラスタはいずれも内部で連結している()
    {
        // 4 つの K4 を鎖状に繋ぐ。
        var edges = Clique(1, 2, 3, 4)
            .Concat(Clique(5, 6, 7, 8))
            .Concat(Clique(9, 10, 11, 12))
            .Concat(Clique(13, 14, 15, 16))
            .Append(Edge(4, 5))
            .Append(Edge(8, 9))
            .Append(Edge(12, 13))
            .ToList();
        var nodes = Nodes(Enumerable.Range(1, 16).ToArray());

        var clusters = LeidenCommunityDetector.Detect(nodes, edges);

        clusters.Should().NotBeEmpty();
        foreach (var cluster in clusters)
            IsConnected(cluster, edges).Should().BeTrue(
                "分断されたコミュニティを生じないことが Leiden を採った理由である（ADR-0035 決定 3）");
    }

    // FR-17 (T-4): **決定性。** 同じ入力を 2 回流すと同じ分割が出る（[[IADR-0425]] 決定 2）。
    // ここが破れると、日次バッチが毎日「構成が変わった」と主張し、
    // `unsummarized-clusters` が恒久的に全件になる。
    [Fact]
    public void 同じ入力からは同じ分割が出る()
    {
        var nodes = Nodes(Enumerable.Range(1, 12).ToArray());
        var edges = Clique(1, 2, 3, 4)
            .Concat(Clique(5, 6, 7, 8))
            .Concat(Clique(9, 10, 11, 12))
            .Append(Edge(4, 5))
            .Append(Edge(8, 9))
            .ToList();

        var first = LeidenCommunityDetector.Detect(nodes, edges);
        var second = LeidenCommunityDetector.Detect(nodes, edges);

        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
    }

    // FR-17 (T-5): **決定性の別軸。** 入力の並び順を変えても分割は変わらない
    // （走査順は文書 ID 昇順で固定してある）。
    [Fact]
    public void 入力の並び順を変えても分割は変わらない()
    {
        var nodes = Nodes(Enumerable.Range(1, 12).ToArray());
        var edges = Clique(1, 2, 3, 4)
            .Concat(Clique(5, 6, 7, 8))
            .Concat(Clique(9, 10, 11, 12))
            .Append(Edge(4, 5))
            .Append(Edge(8, 9))
            .ToList();

        var forward = LeidenCommunityDetector.Detect(nodes, edges);
        var reversed = LeidenCommunityDetector.Detect(
            nodes.AsEnumerable().Reverse().ToList(),
            edges.AsEnumerable().Reverse().ToList());

        reversed.Should().BeEquivalentTo(forward, o => o.WithStrictOrdering());
    }

    // FR-17 (T-7): 辺を 1 本も持たない文書は**単独のクラスタ**になる（境界）。
    // 🔴 **クラスタから消してはならない** —— 孤立文書は `orphan-documents` が数える対象であって、
    // クラスタの母集合から落ちる対象ではない（落とすと未要約クラスタ数の分母が静かにずれる）。
    [Fact]
    public void 孤立した文書は単独のクラスタになる()
    {
        var edges = Clique(1, 2, 3, 4);

        var clusters = LeidenCommunityDetector.Detect(Nodes(1, 2, 3, 4, 9), edges);

        clusters.Should().HaveCount(2);
        clusters.Should().ContainEquivalentOf(Nodes(9));
    }

    // FR-17 (T-7b): 辺が 1 本も無ければ、全文書が単独のクラスタである（境界の外側）。
    [Fact]
    public void 辺が一本も無ければ全文書が単独のクラスタになる()
    {
        var clusters = LeidenCommunityDetector.Detect(Nodes(1, 2, 3), []);

        clusters.Should().HaveCount(3);
        clusters.Should().AllSatisfy(c => c.Should().ContainSingle());
    }

    // FR-17 (T-7c): 文書が 1 件も無ければクラスタも無い（0 件と「測っていない」を混ぜない）。
    [Fact]
    public void 文書が無ければクラスタも無い()
        => LeidenCommunityDetector.Detect([], []).Should().BeEmpty();

    // FR-17, ADR-0035 決定 2 (T-1c): **辺の型の重みが効く。**
    // 同じ形のグラフで橋の重みだけを変えると、橋の両端が同じクラスタに入るかどうかが変わる。
    //
    // 🔴 **「重くすれば全体が 1 つになる」とは書けない。** modularity には解像限界があり、
    // 重すぎる橋はその 1 本を中心に別のコミュニティを作る（実測: 重み 50 では
    // {1,2} {3,4} {5,6} の 3 つに割れる）。**測るのは「重みが検出へ効くこと」であって、
    // 「重ければ必ず併合されること」ではない。**
    [Fact]
    public void 橋の重みは検出結果を変える()
    {
        var nodes = Nodes(1, 2, 3, 4, 5, 6);
        var light = Triangle(1, 2, 3).Concat(Triangle(4, 5, 6)).Append(Edge(3, 4, 0.1)).ToList();
        var heavy = Triangle(1, 2, 3).Concat(Triangle(4, 5, 6)).Append(Edge(3, 4, 50.0)).ToList();

        SameCluster(LeidenCommunityDetector.Detect(nodes, light), 3, 4).Should().BeFalse(
            "軽い橋は塊を跨がせない");
        SameCluster(LeidenCommunityDetector.Detect(nodes, heavy), 3, 4).Should().BeTrue(
            "辺の型による重み付け（ADR-0035 決定 2）が検出へ効いている");
    }

    // ── 器 ─────────────────────────────────────────────────────────────────

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static List<Guid> Nodes(params int[] ns) => [.. ns.Select(Id)];

    private static ClusteringEdge Edge(int a, int b, double w = 1.0) => new(Id(a), Id(b), w);

    private static List<ClusteringEdge> Triangle(int a, int b, int c) =>
        [Edge(a, b), Edge(b, c), Edge(a, c)];

    private static bool SameCluster(IReadOnlyList<IReadOnlyList<Guid>> clusters, int a, int b)
        => clusters.Any(c => c.Contains(Id(a)) && c.Contains(Id(b)));

    private static List<ClusteringEdge> Clique(params int[] ns)
    {
        var edges = new List<ClusteringEdge>();
        for (var i = 0; i < ns.Length; i++)
            for (var j = i + 1; j < ns.Length; j++)
                edges.Add(Edge(ns[i], ns[j]));
        return edges;
    }

    // クラスタの誘導部分グラフが連結か（幅優先）。
    private static bool IsConnected(IReadOnlyList<Guid> cluster, IReadOnlyList<ClusteringEdge> edges)
    {
        if (cluster.Count <= 1)
            return true;

        var inside = cluster.ToHashSet();
        var adjacency = cluster.ToDictionary(c => c, _ => new List<Guid>());
        foreach (var e in edges)
        {
            if (!inside.Contains(e.Source) || !inside.Contains(e.Target) || e.Source == e.Target)
                continue;
            adjacency[e.Source].Add(e.Target);
            adjacency[e.Target].Add(e.Source);
        }

        var seen = new HashSet<Guid> { cluster[0] };
        var queue = new Queue<Guid>([cluster[0]]);
        while (queue.Count > 0)
            foreach (var next in adjacency[queue.Dequeue()])
                if (seen.Add(next))
                    queue.Enqueue(next);

        return seen.Count == cluster.Count;
    }
}
