using GraphService.Infrastructure.Persistence;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, FR-19, ADR-0033 決定 6, ADR-0061 決定 4, [[IADR-0521]] (#1396): 文書をグラフから外すときの辺とタグの片付け。
//
// 使い手は 2 つ —— 露出 OFF の撤収（`GraphDocumentSyncConsumer`）と削除（`DocumentDeletedConsumer`）。
// 手順を 1 か所に置く（片方だけ直すと、撤収した文書だけがタグの件数に残る、といった割れ方をする）。
//
//   [1] この文書を指していたリンクを持つ起点を、この文書を候補から外して作り直す（同名 2 件の曖昧が解ける）。
//   [2] この文書のタグを空にする（タグの件数が減り、上限を下回ったタグの所属文書の辺が戻る）。
//       [1] で作り直した起点の共有タグの辺も揃える。
//   [3] この文書に触れる辺を、出所を問わずすべて消す。
//
// 🔴 **SaveChanges を呼ばない**（呼び出し元が 1 回だけ保存する）。
internal static class GraphDocumentRemoval
{
    internal readonly record struct Result(int EdgesRemoved, int Relinked);

    internal static async Task<Result> DetachAsync(
        GraphDbContext db,
        LinkEdgeSynchronizer links,
        TagEdgeSynchronizer tagEdges,
        Guid documentId,
        string? title,
        CancellationToken ct)
    {
        var relinked = await links.RelinkReferrersAsync(documentId, title, currentTitle: null, ct);
        var tagSync = await tagEdges.SyncAsync(documentId, tags: [], relinked, ct);

        var remaining = (await TrackedEdges.TouchingAsync(db, documentId, ct))
            .Where(TrackedEdges.IsLive)
            .Select(e => e.Entity)
            .ToList();
        db.Edges.RemoveRange(remaining);
        return new Result(tagSync.Removed + remaining.Count, relinked.Count);
    }
}
