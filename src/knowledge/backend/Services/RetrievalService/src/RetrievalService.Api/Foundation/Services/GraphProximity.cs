using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Foundation.Services;

// FR-04, FR-17, UC-10, ADR-0035 決定 2 (#970): 起点からの**近接度**を辺の重みから求める純関数。
//
// 🔴 **近接度は類似度ではない。** 尺度が違うので、ここで求めた値を `SearchResultDto.Score` へ
// 書き戻してはならない（混ぜない。`GraphRerank` が合成としてだけ使う）。
//
// **定義: 近接度 = 起点から当該文書までの経路上の「辺の型の重み」の積の最大値。**
//
// この定義を採る理由は、ADR-0035 決定 2 の 2 つの名指しが**そのまま式の性質になる**ことである。
//   - `supersedes`（重み 1.0）は何ホップ辿っても減衰しない → 「最新版へ**強く**誘導」
//   - `related`（重み 0.3）は 1 ホップで 0.3・2 ホップで 0.09 と急減衰 → 「**弱く**扱う」
// ホップ数の減衰係数を別に持たない —— **重みが減衰を兼ねる**。係数を 2 つ持つと、
// 「どちらで効かせるか」が実装者の気分になる。
public static class GraphProximity
{
    // 起点自身の強さ。**起点の近接度は 0 である**（下記）。
    private const double SeedStrength = 1.0;

    // 起点集合と辺集合から、文書 ID → 近接度（0 より大きい値）の対応を作る。
    //
    // 🔴 **起点自身は結果に含めない（近接度 0）。** 起点は「グラフから得た情報」を 1 つも持たない
    // —— 既にベクトル検索が見つけた文書である。含めると、**ベクトル側の信号を 2 度数える**ことになる。
    public static Dictionary<Guid, double> From(
        IReadOnlyCollection<Guid> seedDocumentIds,
        IReadOnlyList<GraphNeighborEdge> edges,
        int hops)
    {
        var seeds = seedDocumentIds as HashSet<Guid> ?? [.. seedDocumentIds];
        if (seeds.Count == 0 || edges.Count == 0 || hops < 1)
            return [];

        // 隣接表。**辺は無向として扱う**（GraphService の探索がバックリンクを含めて双方向に
        // ロードしており、到達可能性の意味で向きは既に潰れている）。
        var adjacency = new Dictionary<Guid, List<(Guid Far, double Weight)>>();
        foreach (var edge in edges)
        {
            // 重みが 0 以下・NaN の辺は経路として使わない。積が 0 / NaN に汚染されるのを防ぐ
            // （辺の重みは 0.0〜1.0 が値域だが、本ポートの入力は別サービスの応答である）。
            if (!(edge.Weight > 0))
                continue;

            Link(edge.SourceDocumentId, edge.TargetDocumentId, edge.Weight);
            Link(edge.TargetDocumentId, edge.SourceDocumentId, edge.Weight);
        }

        var strength = new Dictionary<Guid, double>();
        foreach (var seed in seeds)
            strength[seed] = SeedStrength;

        var proximity = new Dictionary<Guid, double>();
        var frontier = new HashSet<Guid>(seeds);

        // 幅優先。**同じホップ数の上限（既定 2・上限 3）で打ち切る**（ADR-0034 決定 3）。
        // 打ち切りは GraphService 側でも効いているが、応答が上限を超えて辺を含む場合でも
        // ここで再び効かせる（呼び出し側が上限を守れているかを下流の実装に依存させない）。
        for (var depth = 1; depth <= hops && frontier.Count > 0; depth++)
        {
            var next = new HashSet<Guid>();

            foreach (var near in frontier)
            {
                if (!adjacency.TryGetValue(near, out var neighbors))
                    continue;

                var reach = strength.GetValueOrDefault(near);
                foreach (var (far, weight) in neighbors)
                {
                    // 起点へ戻る辺は候補にしない（上記のとおり起点の近接度は 0）。
                    if (seeds.Contains(far))
                        continue;

                    var candidate = reach * weight;
                    if (candidate <= proximity.GetValueOrDefault(far))
                        continue;

                    // より強い経路が見つかったら更新し、その先も引き直す（最大値を採るため）。
                    proximity[far] = candidate;
                    strength[far] = candidate;
                    next.Add(far);
                }
            }

            frontier = next;
        }

        return proximity;

        void Link(Guid from, Guid to, double weight)
        {
            if (!adjacency.TryGetValue(from, out var list))
                adjacency[from] = list = [];
            list.Add((to, weight));
        }
    }
}
