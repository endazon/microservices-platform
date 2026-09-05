using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-18, ADR-0050 決定 3, IADR-0380 (#1244): 文書の**語の出現数**（類似度候補の材料）を作り直す。
//
// 契機は却下解除・リンク抽出と同じ「本文指紋の変化」である（GraphDocumentSyncConsumer）。同じ 1 回の
// 本文読み取りで出現数も作るので、storage への読み取り経路は増えない。本文が取れないときは表題だけで作る
// —— 辺と違い「消える」ものが無いので縮退してよい（IGraphContentReader の注記は辺の全消しを防ぐためのもの）。
//
// 🔴 **SaveChanges を呼ばない。** 呼び出し元（消費者）が 1 回だけ保存し、ノード upsert・却下解除・辺の差分・
// 出現数を**同一トランザクション**に収める（LinkEdgeSynchronizer と同じ規律）。
//
// 段は 3 段目（`Sync/`）。使う操作が `GraphDocuments/Sync` の 1 つだけである（IADR-0350 と同じ判定）。
public sealed class TermProfileSynchronizer(GraphDbContext db)
{
    public async Task<bool> ExistsAsync(Guid documentId, CancellationToken ct = default)
        => await db.TermProfiles.AsNoTracking().AnyAsync(p => p.DocumentId == documentId, ct);

    // 表題（＋本文）から出現数を作り、行を作るか丸ごと置き換える。
    public async Task UpsertAsync(
        Guid documentId, string title, string? body, string? bodyHash, DateTimeOffset at,
        CancellationToken ct = default)
    {
        var terms = TermProfile.Extract(title, body);

        var existing = await db.TermProfiles.FirstOrDefaultAsync(p => p.DocumentId == documentId, ct);
        if (existing is null)
            db.TermProfiles.Add(GraphDocumentTermProfile.Create(documentId, terms, bodyHash, at));
        else
            existing.Replace(terms, bodyHash, at);
    }
}
