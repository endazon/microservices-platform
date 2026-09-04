using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationService.Domain;
using NotificationService.Common.Options;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Features.Notifications;

// FR-22, IADR-0215 決定 1・2: アプリ内通知の読み出しと既読化。
//
// ★ **本クラスが公開する読み出し口はすべて主体（subject）を必須引数に取り、`Subject == subject`
// で絞る。** 「全件を返してから呼び出し側が絞る」形にしない —— 絞り忘れが 1 箇所でもあれば
// 計画 FR-22 の「通知が所有者本人にのみ届く」が破れ、しかも**肯定形のテストでは緑のまま通る**。
public sealed class NotificationStore(NotificationDbContext db, IOptions<NotificationOptions> options)
{
    private readonly NotificationOptions _options = options.Value;

    // FR-22: 本人宛の通知を新しい順に返す。unreadCount は**絞り込みの影響を受けない全体値**である。
    public async Task<NotificationListDto> ListAsync(
        string subject, bool unreadOnly = false, int? limit = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? _options.DefaultListLimit, 1, _options.MaxListLimit);

        // ★ 本人限定。ここを外すと他人の通知が混ざる（変異試験の対象）。
        var mine = db.Notifications.Where(n => n.Subject == subject);

        var query = unreadOnly ? mine.Where(n => !n.Read) : mine;

        var items = await query
            .OrderByDescending(n => n.OccurredAt)
            .ThenByDescending(n => n.Id)
            .Take(take)
            .ToListAsync(ct);

        var unreadCount = await CountUnreadAsync(subject, ct);

        // 写像の実体は Riok.Mapperly の生成マッパである（IADR-0371 決定 3 / IADR-0377）。
        return new NotificationListDto(items.Select(NotificationMapper.ToDto).ToList(), unreadCount);
    }

    // FR-22: 既読化。**冪等**（既読のものへもう一度呼んでも 200 相当を返す）。
    //
    // ★ **本人の通知でなければ null を返す**（呼び出し側は 404 へ写す）。403 にしない ——
    // 「権限が無い」を返すと、他人の通知 ID が実在することが漏れる（存在秘匿。IADR-0009）。
    public async Task<NotificationReadResultDto?> MarkReadAsync(
        string subject, Guid id, CancellationToken ct = default)
    {
        // ★ 本人限定。Id だけで引くと他人の通知を既読化できてしまう（変異試験の対象）。
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.Subject == subject, ct);

        if (notification is null)
            return null;

        if (!notification.Read)
        {
            notification.MarkRead();
            await db.SaveChangesAsync(ct);
        }

        return new NotificationReadResultDto(notification.Id, await CountUnreadAsync(subject, ct));
    }

    public Task<int> CountUnreadAsync(string subject, CancellationToken ct = default)
        => db.Notifications.CountAsync(n => n.Subject == subject && !n.Read, ct);

    // 手書きの詰め替えは撤去した。写像は `NotificationMapper.ToDto`（Riok.Mapperly の生成マッパ）が
    // 持つ（計画 ADR-0030 §決定 / IADR-0371 決定 3 / IADR-0377）。**このクラスに写像は残さない。**
}
