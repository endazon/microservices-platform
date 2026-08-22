using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Api.Foundation.Persistence;

// FR-17, UC-10, IADR-0242 決定 11: EF Core（PostgreSQL 隣接リスト）によるグラフ読み取り。
//
// ストア製品は計画側で実測待ちのままであり、本実装はポート越しに置いて交換可能に保つ。
public class EfGraphStore(GraphDbContext db) : IGraphStore
{
    public async Task<GraphDocument?> FindNodeAsync(Guid documentId, CancellationToken ct = default)
        => await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == documentId, ct);

    public async Task<IReadOnlyList<GraphDocument>> LoadNodesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return [];

        return await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.DocumentId))
            .ToListAsync(ct);
    }

    // UC-10: フロンティアに接続する辺を**双方向**に一括で読む。
    //
    // 🔴 **引数は AuthorizedNode である。** 非許可ノードから展開できないことを型が保証する
    // （ADR-0034 決定 1 / IADR-0242 決定 2）。署名を Guid へ緩めると型ゲートが無効になる。
    //
    // バックリンク（FR-17）のため source 側・target 側の両方を引く。対称型は書き込み時に
    // (min, max) へ正規化済みなので、双方向に引けば向きに関わらず拾える。
    public async Task<IReadOnlyList<Edge>> LoadIncidentEdgesAsync(
        IReadOnlyList<AuthorizedNode> frontier, CancellationToken ct = default)
    {
        if (frontier.Count == 0)
            return [];

        var ids = frontier.Select(n => n.DocumentId).ToList();

        // ADR-0034 決定 4 / IADR-0242 決定 4: **決定的順序**で返す。
        // 打ち切りが非決定だとテストが flake し、利用者から見て「見えたり見えなかったり」する。
        return await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.SourceDocumentId) || ids.Contains(e.TargetDocumentId))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
    }

    // FR-04, ADR-0035 決定 2 (#947a): 文書ごとの次数。
    //
    // 🔴 **ABAC で絞らない。** 可視の辺だけを数えると、同じ文書が利用者によってハブになったり
    // ならなかったりする。ハブ判定はグラフの構造上の性質である（ポートの注記参照）。
    //
    // 対称辺は (min, max) の 1 行で持つので、source 側・target 側の両方を数えれば
    // 向きに関わらず 1 本として数えられる。**自己ループは持たない**（作成時に拒む）ので
    // 二重計上は起きない。
    public async Task<IReadOnlyDictionary<Guid, int>> LoadDegreesAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return new Dictionary<Guid, int>();

        var ids = documentIds.ToList();

        var asSource = await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.SourceDocumentId))
            .GroupBy(e => e.SourceDocumentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var asTarget = await db.Edges.AsNoTracking()
            .Where(e => ids.Contains(e.TargetDocumentId))
            .GroupBy(e => e.TargetDocumentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var degrees = new Dictionary<Guid, int>();
        foreach (var id in ids) degrees[id] = 0;
        foreach (var r in asSource) degrees[r.Id] = degrees.GetValueOrDefault(r.Id) + r.Count;
        foreach (var r in asTarget) degrees[r.Id] = degrees.GetValueOrDefault(r.Id) + r.Count;
        return degrees;
    }
}
