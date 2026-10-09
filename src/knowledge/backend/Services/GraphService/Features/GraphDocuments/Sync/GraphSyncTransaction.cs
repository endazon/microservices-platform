using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, [[IADR-0521]] 決定 7 (#1396): 購読の受け口 1 通を 1 つのトランザクションに収める。
//
// 共有タグの差分の排他（`ITagEdgeLocks`。PostgreSQL の `pg_advisory_xact_lock`）は**トランザクションの寿命**で持つ。
// 受け口は読み取り（所属・件数）から保存までをこの中で行い、確定で排他を解く。
// 非リレーショナル（単体テストの InMemory）はトランザクションを持たないので null を返す（排他も no-op）。
internal static class GraphSyncTransaction
{
    internal static async Task<IDbContextTransaction?> BeginAsync(GraphDbContext db, CancellationToken ct)
        => db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
}
