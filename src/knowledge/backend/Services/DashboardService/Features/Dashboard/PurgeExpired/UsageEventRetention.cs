using DashboardService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Features.Dashboard.PurgeExpired;

// FR-10, UC-05, SC-10, ADR-0072 決定 3, [[IADR-0368]] (#1198):
// 保持期間（90 日）を過ぎた利用イベントを物理削除する。
//
// 🔴 **`UserId` を落としても行は残る**（ADR-0072 §コンテキスト）。`Query`（検索語）と
// `OccurredAt` の対が無期限に平文で積み上がる。**`ADR-0071` のしきい値は画面の統制であって
// 保管の統制ではない** —— 画面で伏せると決めた語を、保管では 1 件も伏せていなかった。
//
// **削除の基準時刻は集計の起点と一致させる**（ADR-0072 決定 3）。集計は
// `u.OccurredAt >= SinceUtc(days)`（`days` は 90 へクランプ）で読み、ここは
// **その否定**（`u.OccurredAt < SinceUtc(MaxDays)`）で消す。同じ式から得るため、
// **集計に必要な行を落とすことはない**（両者の境界が同じ 1 点である）。
public sealed class UsageEventRetention(DashboardDbContext db)
{
    // 1 周で読み込む上限。**無制限に読み込まない** —— 初回適用時（90 日より古い行が
    // 溜まっている DB）に全件をメモリへ載せると、掃除が原因でサービスが落ちる。
    // 上限に達した周は**残りを次の周へ送る**（消し残しは次周で消える）。
    internal const int BatchSize = 500;

    // ★ `ExecuteDeleteAsync` は使わない —— テストは InMemory プロバイダで走り、
    // InMemory は同 API を実装していない。**同じサービス内の前例**
    // （`KnowledgeHealth/Report` のスナップショット置換）も `RemoveRange` である。
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = CutoffUtc();

        var expired = await db.UsageEvents
            .Where(u => u.OccurredAt < cutoff)
            .OrderBy(u => u.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return 0;

        db.UsageEvents.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    // 削除の基準時刻。**集計の起点そのもの**である（ADR-0072 決定 3）。
    // `UsageRetentionOptions.RetentionDays` は `DashboardEndpoints.MaxDays` であり、
    // 別の定数を置いていない（片方だけ動かせない）。
    internal static DateTimeOffset CutoffUtc()
        => DashboardEndpoints.SinceUtc(UsageRetentionOptions.RetentionDays);
}
