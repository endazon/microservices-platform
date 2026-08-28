using Microsoft.Extensions.Logging;
using NotificationService.Domain;
using NotificationService.Common.Observability;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Features.Notifications;

// FR-22, IADR-0215 決定 3: 通知の発行。
//
// ★ **アプリ内通知の永続化とメールの送信要求を同一トランザクションにしない。**
//   1. アプリ内通知を永続化する（**ここまでが「通知が届いた」の定義である**）
//   2. **別の SaveChanges** でメール送信要求を outbox へ積む
//   3. 実際の送信は EmailOutboxDispatcher が後から行う
//
// 明示トランザクションを開かないので 1 と 2 は別トランザクションであり、
// **2 で何が起きても 1 は取り消されない**。これが計画 FR-22 の受け入れ基準
// 「メールが送れない場合もアプリ内通知は届く」を、テストより前に構造で成り立たせる形である。
public sealed class NotificationPublisher(
    NotificationDbContext db,
    NotificationDeliveryMetrics metrics,
    ILogger<NotificationPublisher> logger)
{
    // 発火の結線（週次バッチ・日次バッチ・イベント購読）は本作業の射程外であり、#451 の解除後に
    // IADR-0215 決定 5 の表のとおり足す。**本メソッドはその 5 経路すべての共通の出口である。**
    public async Task<Notification> PublishAsync(
        string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
        CancellationToken ct = default)
    {
        var notification = Notification.Create(subject, kind, occurredAt, count, thresholdPercent, deadline);

        // ── 段 1: アプリ内通知。**ここが成功したら通知は届いている。**
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        metrics.RecordInApp(kind);

        // ── 段 2: メール送信要求。**失敗しても段 1 には触れない。**
        var outbox = EmailOutboxEntry.For(notification);
        try
        {
            db.EmailOutbox.Add(outbox);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // ★ **握り潰さない。** アプリ内通知は届いたままにしつつ、メール経路が積めなかったことを
            //   記録する（受け入れ基準 5 の「静かに落ちない」はここにも当たる）。
            db.Entry(outbox).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            metrics.RecordEmailOutcome(kind, EmailOutboxStatus.Failed);
            logger.LogError(ex,
                "メール送信要求の outbox 投入に失敗しました。アプリ内通知は届いています。kind={Kind}",
                LogSanitizer.Sanitize(kind));
        }

        return notification;
    }
}
