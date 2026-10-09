using GraphService.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Infrastructure.Persistence;

// FR-17, ADR-0035 決定 1, [[IADR-0521]] 決定 7 (#1396): PostgreSQL のトランザクション寿命の advisory lock
// （`pg_advisory_xact_lock`）による、共有タグの辺の差分の直列化。
//
// - キーは `graph-doc:<ID>` / `graph-tag:<正規化済みのタグ>` を `hashtextextended(…, 0)` で 64 ビットへ畳んだ値。
//   クラスタ検出・健全性のリース（固定キー。セッション寿命の `pg_try_advisory_lock`）とは関数も寿命も別で、
//   64 ビットの空間で偶然一致しても**余分に待つだけ**である（誤って通すことはない）。
// - **待つ**（`try` ではない）。受け口の処理は短く、待たずに諦めると差分を捨てることになる。
// - 🔴 **トランザクションの外では呼ばせない。** 自動確定の文の中で取った xact lock は文の終わりで解け、
//   何も守らない。呼び忘れは例外で知らせる（黙って素通りさせない）。
public sealed class PostgresTagEdgeLocks(GraphDbContext db) : ITagEdgeLocks
{
    public Task LockDocumentAsync(Guid documentId, CancellationToken ct)
        => LockAsync("graph-doc:" + documentId.ToString("D"), ct);

    public async Task LockTagsAsync(IReadOnlyList<string> orderedTags, CancellationToken ct)
    {
        foreach (var tag in orderedTags)
            await LockAsync("graph-tag:" + tag, ct);
    }

    private async Task LockAsync(string key, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "pg_advisory_xact_lock requires an explicit transaction; begin one before syncing tag edges");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}

// 非リレーショナル（単体テストの InMemory 等）向け。advisory lock は PostgreSQL 固有で、InMemory は
// トランザクションも持たない。
public sealed class NoOpTagEdgeLocks : ITagEdgeLocks
{
    public static readonly NoOpTagEdgeLocks Instance = new();

    public Task LockDocumentAsync(Guid documentId, CancellationToken ct) => Task.CompletedTask;

    public Task LockTagsAsync(IReadOnlyList<string> orderedTags, CancellationToken ct) => Task.CompletedTask;
}
