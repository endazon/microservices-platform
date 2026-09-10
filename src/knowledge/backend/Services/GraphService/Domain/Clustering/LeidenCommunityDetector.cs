namespace GraphService.Domain.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3, ADR-0083 決定 1, [[IADR-0425]] (#1363):
// 知識グラフのコミュニティ検出（**Leiden 法**）。
//
// 計画 ADR-0035 決定 3 が手法を確定している ——
// 「**クラスタリング手法は Leiden 法**、実行タイミングは定期バッチ（日次）とする。
//   **Louvain は分断されたコミュニティを生じ得るため採らない**」。
// ADR-0083 決定 1 が「クラスタ ＝ その検出結果」「SC-18 の表示単位も健全性の計数単位も同じもの」と
// 射程を確定した。**本クラスの出力が、その「クラスタ」である。**
//
// ## 段は 3 つ（Leiden の原論文どおり）
//
//   1. **局所移動** … modularity の増分が最大の**隣接**コミュニティへ移す（改善が無くなるまで）
//   2. **細分化**   … 各コミュニティの内部でシングルトンから出発し、**辺のある相手にしか併合しない**
//   3. **集約**     … 細分化した部分コミュニティを 1 ノードへ畳み、上位の分割を初期分割として次段へ
//
// 🔴 **Louvain との差は 2 段目である。** Louvain は 1・3 だけを回すため、
// **集約の前にコミュニティが内部で分断されていても、それを畳んで 1 節点にしてしまう**
// （以後どの段でも直せない）。細分化はシングルトンから出発して**辺のある相手にしか併合しない**ので、
// 畳む単位は必ず連結である。**計画が Louvain を退けた理由がこれである**（ADR-0035 決定 3）。
//
// ⚠️ **正直に書く: 2 段目を外しても、本リポジトリの固定具は赤にならなかった**
// （変異検査で実測。分断が起きるのは論文 Figure 2 のような特定の形であり、合成した小グラフでは
// 再現しなかった）。**それでも 2 段目を置くのは、計画が「Leiden 法」と手法名で確定しており、
// 外せば実装は Louvain になるからである。** 試験で守れていないことは
// `LeidenCommunityDetectorTests` の T-3（連結性の不変条件）と本注記で記録する。
//
// ## 決定性（[[IADR-0425]] 決定 2）
//
// 🔴 **乱択を使わない。** 原論文の細分化は「増分に応じた確率で併合先を選ぶ」が、ここでは
// **増分最大**を採り（θ→0 の極限）、走査順は**文書 ID 昇順**で固定する。
// **同じ入力からは常に同じ分割が出る** —— 日次バッチで毎回クラスタ ID が入れ替わると、
// `unsummarized-clusters` が構成変更を毎日報告し続けて指標として読めなくなる。
//
// 引き換えに、局所最適の質は乱択版よりわずかに劣り得る。**クラスタ数のオーダーが読めれば足りる**
// 用途（ADR-0083 決定 4 の実測）であり、この取引は受け入れる。
internal static class LeidenCommunityDetector
{
    // 増分の比較に使う許容誤差。**厳密に上回るときだけ動かす** ——
    // 同点で動かすと局所移動が循環し、停止しない。
    private const double Epsilon = 1e-12;

    // 集約の段数の上限。実データでは数段で止まる（段ごとにノード数が単調に減る）。
    // **止まらない実装を無限に回さないための安全弁**であり、通常の停止条件ではない。
    private const int MaxLevels = 64;

    // ノード集合と辺集合から、コミュニティ（＝クラスタ）を検出する。
    //
    // 戻り値は**文書 ID の集合の列**。並びは決定的である
    // （各クラスタの中は ID 昇順、クラスタ同士は最小 ID の昇順）。
    // **辺を 1 本も持たない文書は、単独のクラスタになる**（除外しない ——
    // 孤立文書は `orphan-documents` が数える対象であって、クラスタから消える対象ではない）。
    public static IReadOnlyList<IReadOnlyList<Guid>> Detect(
        IEnumerable<Guid> nodes,
        IEnumerable<ClusteringEdge> edges)
    {
        var ordered = nodes.Distinct().Order().ToArray();
        if (ordered.Length == 0)
            return [];

        var indexOf = new Dictionary<Guid, int>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
            indexOf[ordered[i]] = i;

        // 端点が母集合に無い辺は落とす（個人資料を除いた入力では普通に起こる）。
        var mapped = new List<(int A, int B, double W)>();
        foreach (var e in edges)
        {
            if (e.Weight <= 0)
                continue;
            if (!indexOf.TryGetValue(e.Source, out var a) || !indexOf.TryGetValue(e.Target, out var b))
                continue;
            mapped.Add((a, b, e.Weight));
        }

        var graph = LevelGraph.Build(ordered.Length, mapped);

        // 辺が 1 本も無ければ全員が単独のクラスタである（modularity の分母が 0 になるため、
        // 数式へ入れる前にここで返す）。
        if (graph.TwoM <= 0)
            return [.. ordered.Select(id => (IReadOnlyList<Guid>)[id])];

        // 現在の段のノードが、元のどの文書を含むか。
        var members = new List<List<int>>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
            members.Add([i]);

        // 初期分割はシングルトン。
        var community = new int[graph.Count];
        for (var i = 0; i < graph.Count; i++)
            community[i] = i;

        var assignment = new int[ordered.Length];

        for (var level = 0; level < MaxLevels; level++)
        {
            var moved = LocalMove(graph, community);
            Flatten(members, community, assignment);

            // 全ノードが単独のコミュニティなら、畳む相手が居ない。
            if (DistinctCount(community) == graph.Count)
                break;

            // 🔴 2 段目。ここが Leiden である。
            var refined = Refine(graph, community);
            var aggregated = Aggregate(graph, community, refined, members);

            // 段を進めてもノード数が減らず、局所移動も何も動かさなかったなら収束している。
            if (aggregated.Graph.Count == graph.Count && !moved)
                break;

            graph = aggregated.Graph;
            community = aggregated.Community;
            members = aggregated.Members;
        }

        return Group(ordered, assignment);
    }

    // ── 1 段目: 局所移動 ────────────────────────────────────────────────────
    //
    // 「動かした節点の隣人を待ち行列へ戻す」形（原論文の move nodes fast）。
    // 戻り値は 1 つでも動かしたか。
    private static bool LocalMove(LevelGraph graph, int[] community)
    {
        var tot = new double[graph.Count];
        var size = new int[graph.Count];
        for (var i = 0; i < graph.Count; i++)
        {
            tot[community[i]] += graph.Degree[i];
            size[community[i]]++;
        }

        // 空きコミュニティ番号（「単独へ抜ける」選択肢に使う）。
        // **初期分割がシングルトンでない段では、最初から空いている番号がある** ——
        // 取りこぼすと「単独へ抜ける」が選べなくなるので、ここで全部積む。
        var free = new Stack<int>();
        for (var c = graph.Count - 1; c >= 0; c--)
            if (size[c] == 0)
                free.Push(c);

        var queue = new Queue<int>(Enumerable.Range(0, graph.Count));
        var queued = new bool[graph.Count];
        Array.Fill(queued, true);

        var weightToCommunity = new Dictionary<int, double>();
        var movedAny = false;

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            queued[node] = false;

            var origin = community[node];
            var degree = graph.Degree[node];

            tot[origin] -= degree;
            if (--size[origin] == 0)
                free.Push(origin);

            weightToCommunity.Clear();
            foreach (var (neighbor, weight) in graph.Adjacency[node])
            {
                var c = community[neighbor];
                weightToCommunity[c] = weightToCommunity.GetValueOrDefault(c) + weight;
            }

            // 出発点は「元のコミュニティに戻る」。**同点では動かさない**（循環を作らない）。
            var best = origin;
            var bestGain = Gain(weightToCommunity, tot, origin, degree, graph.TwoM);

            // 「単独へ抜ける」選択肢。空きが要るのは元のコミュニティに他の節点が残る場合だけで、
            // そのときは鳩の巣原理により必ず空き番号がある。
            var alone = size[origin] == 0 ? origin : TakeFree(free, size);
            if (alone != origin && 0 > bestGain + Epsilon)
            {
                best = alone;
                bestGain = 0;
            }

            // **候補は隣接コミュニティだけである。** 辺の無いコミュニティは `k_i,in = 0` なので
            // 増分が必ず負になり、そもそも選ばれない —— したがってこの制限は**枝刈り**であって
            // 意味を変えない（変異検査で確認済み。全コミュニティを候補にしても結果は変わらなかった）。
            // 連結性を担保しているのは、この「辺が無ければ増分が負」という性質そのものである。
            foreach (var candidate in weightToCommunity.Keys.Order())
            {
                if (candidate == origin)
                    continue;
                var gain = Gain(weightToCommunity, tot, candidate, degree, graph.TwoM);
                if (gain > bestGain + Epsilon)
                {
                    best = candidate;
                    bestGain = gain;
                }
            }

            community[node] = best;
            tot[best] += degree;
            size[best]++;

            if (best == origin)
                continue;

            movedAny = true;
            foreach (var (neighbor, _) in graph.Adjacency[node])
            {
                if (community[neighbor] == best || queued[neighbor])
                    continue;
                queued[neighbor] = true;
                queue.Enqueue(neighbor);
            }
        }

        return movedAny;
    }

    // ── 2 段目: 細分化（Leiden の要点） ─────────────────────────────────────
    //
    // 各コミュニティの**内部だけ**で、シングルトンから出発して貪欲に併合する。
    // 併合先は**辺のある部分コミュニティに限る**ため、出来上がる部分コミュニティは必ず連結である。
    //
    // 原論文は「増分に応じた確率で選ぶ」が、ここでは増分最大を採る（決定性。冒頭の注記）。
    private static int[] Refine(LevelGraph graph, int[] community)
    {
        var refined = new int[graph.Count];
        var tot = new double[graph.Count];
        var size = new int[graph.Count];
        for (var i = 0; i < graph.Count; i++)
        {
            refined[i] = i;
            tot[i] = graph.Degree[i];
            size[i] = 1;
        }

        var byCommunity = new Dictionary<int, List<int>>();
        for (var i = 0; i < graph.Count; i++)
            (byCommunity.TryGetValue(community[i], out var bucket)
                ? bucket
                : byCommunity[community[i]] = []).Add(i);

        var weightToRefined = new Dictionary<int, double>();

        foreach (var group in byCommunity.Keys.Order())
        {
            foreach (var node in byCommunity[group])
            {
                // **まだ単独の節点だけを動かす**（原論文と同じ。動かした先を再び割らない）。
                if (size[refined[node]] != 1)
                    continue;

                var origin = refined[node];
                var degree = graph.Degree[node];
                tot[origin] -= degree;
                size[origin]--;

                weightToRefined.Clear();
                foreach (var (neighbor, weight) in graph.Adjacency[node])
                {
                    // 🔴 **同じコミュニティの中だけを見る。** ここを外すと細分化にならない。
                    if (community[neighbor] != group)
                        continue;
                    var t = refined[neighbor];
                    weightToRefined[t] = weightToRefined.GetValueOrDefault(t) + weight;
                }

                var best = origin;
                var bestGain = 0.0;
                foreach (var candidate in weightToRefined.Keys.Order())
                {
                    if (candidate == origin)
                        continue;
                    var gain = Gain(weightToRefined, tot, candidate, degree, graph.TwoM);
                    if (gain > bestGain + Epsilon)
                    {
                        best = candidate;
                        bestGain = gain;
                    }
                }

                refined[node] = best;
                tot[best] += degree;
                size[best]++;
            }
        }

        return refined;
    }

    // ── 3 段目: 集約 ───────────────────────────────────────────────────────
    //
    // 細分化した部分コミュニティを 1 ノードへ畳む。**上位（細分化前）の分割を初期分割として引き継ぐ**
    // のが Leiden であり、これにより細分化で割った断片が上位の判断へ戻れる。
    private static Aggregation Aggregate(
        LevelGraph graph, int[] community, int[] refined, List<List<int>> members)
    {
        // 決定的な番号づけ: 部分コミュニティを「最小の節点番号」の昇順に並べる
        // （節点を 0 から走査し、初めて見た順に番号を振る）。
        var newIndex = new Dictionary<int, int>();
        for (var i = 0; i < graph.Count; i++)
            if (!newIndex.ContainsKey(refined[i]))
                newIndex[refined[i]] = newIndex.Count;

        var count = newIndex.Count;
        var newMembers = new List<List<int>>(count);
        for (var i = 0; i < count; i++)
            newMembers.Add([]);
        var inherited = new int[count];
        var edges = new List<(int A, int B, double W)>();

        for (var i = 0; i < graph.Count; i++)
        {
            var ri = newIndex[refined[i]];
            newMembers[ri].AddRange(members[i]);
            inherited[ri] = community[i];

            if (graph.SelfLoop[i] > 0)
                edges.Add((ri, ri, graph.SelfLoop[i]));

            foreach (var (neighbor, weight) in graph.Adjacency[i])
            {
                // 無向の対を 1 回だけ数える。
                if (neighbor < i)
                    continue;
                var rj = newIndex[refined[neighbor]];
                edges.Add((ri, rj, weight));
            }
        }

        // 引き継いだコミュニティ番号を 0..k-1 へ詰め直す（空き番号を「単独へ抜ける」用に空ける）。
        var renumber = new Dictionary<int, int>();
        var newCommunity = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (!renumber.TryGetValue(inherited[i], out var c))
                renumber[inherited[i]] = c = renumber.Count;
            newCommunity[i] = c;
        }

        foreach (var bucket in newMembers)
            bucket.Sort();

        return new Aggregation(LevelGraph.Build(count, edges), newCommunity, newMembers);
    }

    // ── 器 ─────────────────────────────────────────────────────────────────

    // modularity の増分（定数倍を落とした形）。
    // ΔQ ∝ k_i,in − Σ_tot(C) · k_i / 2m。**同じ式を局所移動と細分化で共有する。**
    private static double Gain(
        Dictionary<int, double> weightTo, double[] tot, int candidate, double degree, double twoM)
        => weightTo.GetValueOrDefault(candidate) - (tot[candidate] * degree / twoM);

    // 空きコミュニティ番号を 1 つ取る。積んだ後に埋まった番号は捨てる（遅延削除）。
    private static int TakeFree(Stack<int> free, int[] size)
    {
        while (free.Count > 0)
        {
            var candidate = free.Pop();
            if (size[candidate] == 0)
                return candidate;
        }

        // 到達しない（元のコミュニティに他の節点が残る＝どこかが空いている）。
        // 万一到達しても停止性は保たれるよう、動かさない選択肢（-1）ではなく例外にしない。
        return Array.FindIndex(size, s => s == 0);
    }

    private static int DistinctCount(int[] community) => community.Distinct().Count();

    // 現在の段のコミュニティ割当を、元の文書の割当へ落とす。
    private static void Flatten(List<List<int>> members, int[] community, int[] assignment)
    {
        for (var i = 0; i < members.Count; i++)
            foreach (var original in members[i])
                assignment[original] = community[i];
    }

    private static IReadOnlyList<IReadOnlyList<Guid>> Group(Guid[] ordered, int[] assignment)
    {
        var buckets = new Dictionary<int, List<Guid>>();
        for (var i = 0; i < ordered.Length; i++)
            (buckets.TryGetValue(assignment[i], out var bucket)
                ? bucket
                : buckets[assignment[i]] = []).Add(ordered[i]);

        // 各クラスタの中は ID 昇順（上の走査が既に昇順）、クラスタ同士は最小 ID の昇順。
        return [.. buckets.Values.OrderBy(b => b[0]).Select(b => (IReadOnlyList<Guid>)b)];
    }

    private sealed record Aggregation(LevelGraph Graph, int[] Community, List<List<int>> Members);

    // 1 段ぶんの重み付き無向グラフ。自己ループは次数へ 2 回数える（集約で生まれる）。
    private sealed class LevelGraph
    {
        private LevelGraph(Dictionary<int, double>[] adjacency, double[] selfLoop, double[] degree)
        {
            Adjacency = adjacency;
            SelfLoop = selfLoop;
            Degree = degree;
            TwoM = degree.Sum();
        }

        public int Count => Degree.Length;

        public Dictionary<int, double>[] Adjacency { get; }

        public double[] SelfLoop { get; }

        public double[] Degree { get; }

        public double TwoM { get; }

        public static LevelGraph Build(int count, IEnumerable<(int A, int B, double W)> edges)
        {
            var adjacency = new Dictionary<int, double>[count];
            for (var i = 0; i < count; i++)
                adjacency[i] = [];
            var selfLoop = new double[count];

            foreach (var (a, b, w) in edges)
            {
                if (a == b)
                {
                    selfLoop[a] += w;
                    continue;
                }

                adjacency[a][b] = adjacency[a].GetValueOrDefault(b) + w;
                adjacency[b][a] = adjacency[b].GetValueOrDefault(a) + w;
            }

            var degree = new double[count];
            for (var i = 0; i < count; i++)
                degree[i] = adjacency[i].Values.Sum() + (2 * selfLoop[i]);

            return new LevelGraph(adjacency, selfLoop, degree);
        }
    }
}

// クラスタリングの入力となる 1 本の辺。**無向**として扱う（コミュニティは向きを持たない）。
// 重みは辺の型の `EdgeType.Weight`（ADR-0035 決定 2 の「辺の型による重み付け」）。
internal readonly record struct ClusteringEdge(Guid Source, Guid Target, double Weight);
