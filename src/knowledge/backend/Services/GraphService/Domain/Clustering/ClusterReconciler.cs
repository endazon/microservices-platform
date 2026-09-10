namespace GraphService.Domain.Clustering;

// FR-17, SC-10, ADR-0035 決定 6, ADR-0083 決定 3 条件 2, [[IADR-0425]] 決定 3 (#1363):
// **今日の検出結果と、昨日まで永続化されているクラスタを対応づける。**
//
// なぜ要るか —— ADR-0083 決定 3 の条件 2 は「**クラスタ構成が変わった後に再生成されていない**」で
// あり、これを判定するには「**昨日のあのクラスタと、今日のこのクラスタは同じものか**」が要る。
// 検出結果は文書 ID の集合でしかなく、識別子を自分では持たない。
//
// 対応づけの規則（[[IADR-0425]] 決定 3）:
//
//   1. 既存クラスタと検出結果の全対について **Jaccard 係数**を出す。
//   2. **係数の降順に貪欲**へ確定する（同点は既存クラスタ ID・検出結果の順で決定的に割る）。
//   3. 🔴 **0.5 未満は対応づけない**。過半の構成員を共有しないものを「同じクラスタ」と呼ぶと、
//      要約が別物のクラスタへ引き継がれる。
//   4. 対応がつかない検出結果は**新しいクラスタ**、対応がつかない既存クラスタは**消滅**である。
internal static class ClusterReconciler
{
    // 「同じクラスタ」と見なす最小の Jaccard 係数。**過半**（0.5）を境にする。
    public const double MinimumJaccard = 0.5;

    public static ClusterReconciliation Reconcile(
        IReadOnlyList<IReadOnlyList<Guid>> detected,
        IReadOnlyList<ExistingCluster> existing)
    {
        var detectedSets = detected.Select(d => d.ToHashSet()).ToArray();
        var existingSets = existing.Select(e => e.Members.ToHashSet()).ToArray();

        var pairs = new List<(double Jaccard, int Existing, int Detected)>();
        for (var e = 0; e < existingSets.Length; e++)
        {
            for (var d = 0; d < detectedSets.Length; d++)
            {
                var intersection = existingSets[e].Count(m => detectedSets[d].Contains(m));
                if (intersection == 0)
                    continue;
                var union = existingSets[e].Count + detectedSets[d].Count - intersection;
                var jaccard = (double)intersection / union;
                if (jaccard < MinimumJaccard)
                    continue;
                pairs.Add((jaccard, e, d));
            }
        }

        // 決定的な貪欲: 係数の降順 → 既存クラスタ ID の昇順 → 検出結果の並び順。
        pairs.Sort((x, y) =>
        {
            var byJaccard = y.Jaccard.CompareTo(x.Jaccard);
            if (byJaccard != 0)
                return byJaccard;
            var byExisting = existing[x.Existing].ClusterId.CompareTo(existing[y.Existing].ClusterId);
            return byExisting != 0 ? byExisting : x.Detected.CompareTo(y.Detected);
        });

        var takenExisting = new bool[existingSets.Length];
        var takenDetected = new bool[detectedSets.Length];
        var unchanged = new List<ReconciledCluster>();
        var changed = new List<ReconciledCluster>();

        foreach (var (_, e, d) in pairs)
        {
            if (takenExisting[e] || takenDetected[d])
                continue;
            takenExisting[e] = true;
            takenDetected[d] = true;

            var reconciled = new ReconciledCluster(existing[e].ClusterId, detected[d]);
            // 構成が同一か（件数だけで比べない —— 同数の入れ替わりを見落とす）。
            if (existingSets[e].SetEquals(detectedSets[d]))
                unchanged.Add(reconciled);
            else
                changed.Add(reconciled);
        }

        var added = detected.Where((_, d) => !takenDetected[d]).ToList();
        var removed = existing.Where((_, e) => !takenExisting[e]).Select(e => e.ClusterId).ToList();

        return new ClusterReconciliation(unchanged, changed, added, removed);
    }
}

internal sealed record ExistingCluster(Guid ClusterId, IReadOnlyList<Guid> Members);

internal sealed record ReconciledCluster(Guid ClusterId, IReadOnlyList<Guid> Members);

internal sealed record ClusterReconciliation(
    IReadOnlyList<ReconciledCluster> Unchanged,
    IReadOnlyList<ReconciledCluster> Changed,
    IReadOnlyList<IReadOnlyList<Guid>> Added,
    IReadOnlyList<Guid> Removed);
