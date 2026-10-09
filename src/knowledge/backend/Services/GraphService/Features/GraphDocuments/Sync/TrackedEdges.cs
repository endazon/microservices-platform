using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, [[IADR-0522]] (#1396): **同じ保存の中の未保存の変更を含めて**、文書に触れる辺を見る。
//
// 1 通の `DocumentUpdated` の処理は、本文のリンクの差分・後着のリンクの作り直し・共有タグの差分を
// **1 回の保存**に収める（途中で保存すると「辺は入ったがノードは入らなかった」が作れる）。
// したがって後段は、前段が追加した（まだ DB に無い）辺と、前段が削除した（まだ DB に在る）辺を
// 見分けなければならない。DB への問い合わせだけでは前者が見えず、同じ 5 つ組の辺を 2 度追加して
// 一意索引 `ux_edges` に当たる。
//
// 読み込んだうえで変更追跡の項目を返す。**状態（Added / Deleted / Unchanged / Modified）は呼び出し側が見る。**
internal static class TrackedEdges
{
    internal static async Task<List<EntityEntry<Edge>>> TouchingAsync(
        GraphDbContext db, Guid documentId, CancellationToken ct)
    {
        await db.Edges
            .Where(e => e.SourceDocumentId == documentId || e.TargetDocumentId == documentId)
            .LoadAsync(ct);
        return db.ChangeTracker.Entries<Edge>()
            .Where(e => e.Entity.SourceDocumentId == documentId || e.Entity.TargetDocumentId == documentId)
            .ToList();
    }

    internal static bool IsLive(EntityEntry<Edge> entry)
        => entry.State is not (EntityState.Deleted or EntityState.Detached);
}

// ux_edges（一意索引）と同じ 5 つ組。**Edge.Create の正規化後の値で作る。**
internal readonly record struct EdgeKey(
    Guid Source, Guid Target, Guid EdgeTypeId, string SourceAnchor, string TargetAnchor)
{
    internal static EdgeKey Of(Edge e)
        => new(e.SourceDocumentId, e.TargetDocumentId, e.EdgeTypeId, e.SourceAnchor, e.TargetAnchor);
}
