using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationService.Api.Foundation.Options;
using NotificationService.Api.Foundation.Persistence;

namespace NotificationService.Api.Foundation.Services;

// FR-22, IADR-0215 決定 2: 保持期間（既定 90 日）を過ぎたアプリ内通知を物理削除する。
//
// **90 日は計画に根拠が無い実装側の判断である**（個人資料の論理削除の保管期間 = ADR-0037 決定 5 へ
// 揃えた）。運用で不足が出たら改定 IADR を要する。
//
// **outbox は消さない。** 送出の記録は観測面（SC-10）であり、通知本体より長く残る必要がある。
public sealed class NotificationRetention(NotificationDbContext db, IOptions<NotificationOptions> options)
{
    private readonly NotificationOptions _options = options.Value;

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now.AddDays(-_options.RetentionDays);
        var expired = await db.Notifications.Where(n => n.OccurredAt < cutoff).ToListAsync(ct);
        if (expired.Count == 0)
            return 0;

        db.Notifications.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
