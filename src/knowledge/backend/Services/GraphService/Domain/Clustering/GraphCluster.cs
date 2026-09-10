namespace GraphService.Domain.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3・6, ADR-0083 決定 1・3, [[IADR-0425]] (#1363):
// 日次バッチが検出したクラスタ（＝コミュニティ）1 個。
//
// 🔴 **要約そのものは持たない。** ADR-0035 決定 5 が「コミュニティ要約は別系統として持つ
// （索引化しない）」と定めている。ここが持つのは**再生成の判定に要る時刻だけ**である。
//
// `CompositionChangedAt` は ADR-0083 決定 3 の条件 2（**クラスタ構成が変わった後に再生成されて
// いない**）を判定するための時刻である。**検出のたびに前進させてはならない** ——
// 日次バッチは毎日走るので、毎回前進させると全クラスタが恒久的に「未要約」になる。
public class GraphCluster
{
    public Guid ClusterId { get; private set; } = Guid.NewGuid();

    // 初めて検出された時刻。**同一性が保たれる限り変わらない**（ClusterReconciler が対応づける）。
    public DateTimeOffset DetectedAt { get; private set; }

    // 構成（所属文書の集合）が最後に変わった時刻。
    public DateTimeOffset CompositionChangedAt { get; private set; }

    // 所属文書数。**表示・監査のための冗長列**であり、判定には使わない
    // （判定は graph_cluster_members の実体で行う。件数だけで比べると入れ替わりを見落とす）。
    public int MemberCount { get; private set; }

    public static GraphCluster Create(DateTimeOffset now, int memberCount) => new()
    {
        DetectedAt = now,
        CompositionChangedAt = now,
        MemberCount = memberCount,
    };

    // 構成が変わったときだけ呼ぶ。
    public void MarkCompositionChanged(DateTimeOffset now, int memberCount)
    {
        CompositionChangedAt = now;
        MemberCount = memberCount;
    }
}

// クラスタへの所属。**graph_documents への外部キーは張らない**（edges・document_link_targets と
// 同じ理由 —— ノード同期とイベントの到着順に人工的な依存を作らない）。
public class GraphClusterMember
{
    public Guid ClusterId { get; private set; }

    public Guid DocumentId { get; private set; }

    public static GraphClusterMember Create(Guid clusterId, Guid documentId) => new()
    {
        ClusterId = clusterId,
        DocumentId = documentId,
    };
}
