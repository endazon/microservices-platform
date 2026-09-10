using GraphService.Domain;
using GraphService.Domain.Clustering;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.Clustering.Detect;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3・6・8, ADR-0083 決定 1, [[IADR-0425]] (#1363):
// **日次バッチ 1 周期ぶんの仕事** —— 共有グラフを読み、Leiden 法でクラスタを検出し、
// 既存のクラスタと対応づけて永続化する。
//
// ## 入力から個人資料を落とす
//
// 🔴 ADR-0035 決定 8 が「**共有グラフは個人資料を含まない 1 つとする**（決定 3 のクラスタリング
// 入力除外と同じ）」と明記している。判定は `GraphDocumentScope.IsPrivateNote` ——
// **集合帰属で判定する。「organization でない」と書いてはならない**（`doc_scope` を持たない
// 既存文書がすべて個人資料に倒れ、共有グラフが空になる）。
//
// ## 重みは辺の型が持つ
//
// ADR-0035 決定 2「**辺の型による重み付け**（`supersedes` は最新版へ強く誘導し、`related` は弱く扱う）」
// をそのまま入力にする。同じ文書対に複数の辺があれば重みは合算される（検出器の中で足す）。
public sealed class ClusterDetectionJob(
    GraphDbContext db,
    TimeProvider clock,
    ILogger<ClusterDetectionJob> logger)
{
    public async Task<ClusterDetectionResult> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var documents = await db.Documents.AsNoTracking()
            .Select(d => new { d.DocumentId, d.Attributes })
            .ToListAsync(ct);

        // 🔴 個人資料は共有グラフに載せない（ADR-0035 決定 8）。
        var nodes = documents
            .Where(d => !GraphDocumentScope.IsPrivateNote(d.Attributes))
            .Select(d => d.DocumentId)
            .ToHashSet();

        var edgeRows = await db.Edges.AsNoTracking()
            .Join(db.EdgeTypes.AsNoTracking(), e => e.EdgeTypeId, t => t.Id,
                (e, t) => new { e.SourceDocumentId, e.TargetDocumentId, t.Weight })
            .ToListAsync(ct);

        // 端点の片方でも共有グラフの外なら落とす（個人資料から組織文書へ張られた辺）。
        var edges = edgeRows
            .Where(e => nodes.Contains(e.SourceDocumentId) && nodes.Contains(e.TargetDocumentId))
            .Select(e => new ClusteringEdge(e.SourceDocumentId, e.TargetDocumentId, e.Weight))
            .ToList();

        var detected = LeidenCommunityDetector.Detect(nodes, edges);

        var clusters = await db.Clusters.ToListAsync(ct);
        var memberRows = await db.ClusterMembers.ToListAsync(ct);
        var membersByCluster = memberRows
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.DocumentId).ToList());

        var existing = clusters
            .Select(c => new ExistingCluster(
                c.ClusterId, membersByCluster.GetValueOrDefault(c.ClusterId, [])))
            .ToList();

        var reconciliation = ClusterReconciler.Reconcile(detected, existing);
        var byId = clusters.ToDictionary(c => c.ClusterId);

        // 消滅したクラスタ。**所属・要約も明示的に消す** —— カスケードに頼ると、
        // 依存側を読み込んでいないプロバイダ（InMemory）で孤児が残る。
        if (reconciliation.Removed.Count > 0)
        {
            var removed = reconciliation.Removed.ToHashSet();
            db.ClusterMembers.RemoveRange(memberRows.Where(m => removed.Contains(m.ClusterId)));
            db.ClusterSummaries.RemoveRange(
                await db.ClusterSummaries.Where(s => removed.Contains(s.ClusterId)).ToListAsync(ct));
            db.Clusters.RemoveRange(reconciliation.Removed.Select(id => byId[id]));
        }

        // 構成が変わったクラスタ。**同一性は保つ**（要約はここまでの分が引き継がれ、
        // `CompositionChangedAt` が前進することで「未要約」に数えられる）。
        foreach (var cluster in reconciliation.Changed)
        {
            byId[cluster.ClusterId].MarkCompositionChanged(now, cluster.Members.Count);
            db.ClusterMembers.RemoveRange(
                memberRows.Where(m => m.ClusterId == cluster.ClusterId));
            db.ClusterMembers.AddRange(
                cluster.Members.Select(m => GraphClusterMember.Create(cluster.ClusterId, m)));
        }

        // 🔴 **構成が変わっていないクラスタには触らない。** 検出のたびに時刻を前進させると、
        // 日次バッチが毎日「構成が変わった」と主張し、全クラスタが恒久的に未要約になる。
        foreach (var members in reconciliation.Added)
        {
            var cluster = GraphCluster.Create(now, members.Count);
            db.Clusters.Add(cluster);
            db.ClusterMembers.AddRange(
                members.Select(m => GraphClusterMember.Create(cluster.ClusterId, m)));
        }

        await db.SaveChangesAsync(ct);

        var result = new ClusterDetectionResult(
            detected.Count,
            reconciliation.Unchanged.Count,
            reconciliation.Changed.Count,
            reconciliation.Added.Count,
            reconciliation.Removed.Count);

        logger.LogInformation(
            "知識グラフのクラスタを検出した（clusters={Clusters} unchanged={Unchanged} "
            + "changed={Changed} added={Added} removed={Removed} nodes={Nodes} edges={Edges}）。"
            + "**個人資料は入力に含まない**（ADR-0035 決定 8）。",
            result.Detected, result.Unchanged, result.Changed, result.Added, result.Removed,
            nodes.Count, edges.Count);

        return result;
    }
}

// 1 周期の結果。**ログと検証のための値**であり、指標の生産者はこれを使わない
// （未要約クラスタ数は `KnowledgeHealthCollector` が永続化された行から数える）。
public sealed record ClusterDetectionResult(
    int Detected, int Unchanged, int Changed, int Added, int Removed);
