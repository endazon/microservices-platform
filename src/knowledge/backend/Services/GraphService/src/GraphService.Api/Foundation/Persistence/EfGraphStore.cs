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
}
