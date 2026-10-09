using GraphService.Domain;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, ADR-0035 決定 1, [[IADR-0521]] (#1396): 共有タグの辺の構成。
//
// `MaxDocumentsPerTag` — 文書数がこれを**超える**タグからは辺を作らない（`TagEdgeRule.IsHub`）。
// 既定は探索のハブ次数上限（`GraphTraversal.MaxHubDegree`）と同じ 50。
// **不正値（2 未満・上限超）は既定へ倒し、起動は落とさない** —— 起動を落とすと DocumentUpdated /
// DocumentDeleted の購読ごと止まる（`KnowledgeHealthOptions` / `ClusterDetectionOptions` と同じ向き）。
public sealed class TagEdgeOptions
{
    public const string SectionName = "TagEdges";
    public const int DefaultMaxDocumentsPerTag = GraphTraversal.MaxHubDegree;
    // 1 タグあたり最大 約 50 万本（1000×999/2）。これ以上は 1 通の処理で引く行が多すぎる。
    public const int UpperBound = 1000;

    public int MaxDocumentsPerTag { get; set; } = DefaultMaxDocumentsPerTag;

    public int EffectiveMaxDocumentsPerTag
        => MaxDocumentsPerTag is >= 2 and <= UpperBound ? MaxDocumentsPerTag : DefaultMaxDocumentsPerTag;
}
