using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Api.Foundation.Domain;
using NotificationService.Api.Foundation.Observability;
using NotificationService.Api.Foundation.Options;
using NotificationService.Api.Foundation.Persistence;
using NotificationService.Api.Foundation.Ports;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace NotificationService.Api.Foundation.Services;

// FR-22, SC-10, ADR-0045 決定 3・8, IADR-0215 決定 4: outbox の送出とレート制御。
//
// ★ **計画 FR-22 の受け入れ基準「送信上限を超える通知が静かに落ちない」への回答が本クラスである。**
// 4 つの結末をすべて状態・監査ログ・メトリクスの 3 箇所に残す。
//   sent     … 送れた
//   deferred … 日次上限に触れたので**繰り越した**（アプリ内通知は既に届いており、メールは補助なので
//               遅れて届いても意味が残る。ADR-0045 §結果 フォローアップ 8 への回答）
//   dropped  … **期限を過ぎた繰り越し**（完全削除の 7 日前通知が完全削除の後に届いても意味が無い）。
//               **黙って消さない** —— 理由つきで記録する
//   failed   … 送信そのものが失敗した（SMTP 未設定を含む。**未設定は成功ではない**）
//
// 上限の消費を数える場所が 1 つであることが、この設計の要である（IADR-0215 決定 1 の決め手）。
public sealed class EmailOutboxDispatcher(
    NotificationDbContext db,
    IEmailTransport transport,
    IEmailAddressResolver addressResolver,
    NotificationDeliveryMetrics metrics,
    IAuditLogger audit,
    IOptions<NotificationOptions> options,
    ILogger<EmailOutboxDispatcher> logger)
{
    public const string AuditActionPrefix = "notification.email.";
    public const string LimitReachedAction = "notification.email.limit_reached";

    public const string ReasonDailyLimit = "daily-limit-reached";
    public const string ReasonDeadlinePassed = "deadline-passed";
    public const string ReasonNoAddress = "recipient-address-unresolved";

    private readonly NotificationOptions _options = options.Value;

    // 送出待ち（pending / deferred）を古い順に処理する。**戻り値は結末の内訳**である。
    public async Task<DispatchSummary> DispatchPendingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var limit = _options.DailyEmailLimit;

        // ★ 上限は「その日に送った件数」で数える。**送出待ちの件数では数えない** ——
        //   繰り越したものを二重に数えると、翌日も送れないまま溜まり続ける。
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var sentToday = await db.EmailOutbox.CountAsync(
            o => o.Status == EmailOutboxStatus.Sent && o.SentAt != null && o.SentAt >= dayStart, ct);

        var pending = await db.EmailOutbox
            .Where(o => o.Status == EmailOutboxStatus.Pending || o.Status == EmailOutboxStatus.Deferred)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync(ct);

        var summary = new DispatchSummary();
        var limitAnnounced = false;

        foreach (var entry in pending)
        {
            // 判定 1: **期限切れの繰り越しは破棄する**（繰り越しても意味を失っているため）。
            if (entry.Deadline is { } deadline && deadline <= now)
            {
                entry.Drop(now, ReasonDeadlinePassed);
                Record(entry, EmailOutboxStatus.Dropped, ReasonDeadlinePassed);
                summary.Dropped++;
                continue;
            }

            // 判定 2: **日次上限に触れたら送らずに繰り越す。**
            if (sentToday >= limit)
            {
                if (!limitAnnounced)
                {
                    // 上限到達そのものを 1 事象として残す（ADR-0045 決定 8）。
                    metrics.RecordLimitReached();
                    audit.Record(LimitReachedAction, "system", "reached",
                        $"limit={limit} pending={pending.Count}");
                    logger.LogWarning(
                        "メールの日次送信上限に達しました。以降は繰り越します。limit={Limit} pending={Pending}",
                        limit, pending.Count);
                    limitAnnounced = true;
                }

                entry.Defer(now, ReasonDailyLimit);
                Record(entry, EmailOutboxStatus.Deferred, ReasonDailyLimit);
                summary.Deferred++;
                continue;
            }

            // 判定 3: 送る。**宛先が解決できないのも「送れなかった」であって成功ではない。**
            var address = await addressResolver.ResolveAsync(entry.Subject, ct);
            if (string.IsNullOrWhiteSpace(address))
            {
                entry.Fail(now, ReasonNoAddress);
                Record(entry, EmailOutboxStatus.Failed, ReasonNoAddress);
                summary.Failed++;
                continue;
            }

            var message = new EmailMessage(
                entry.Subject, entry.Kind, entry.Count, entry.ThresholdPercent, entry.Deadline,
                address, NotificationMailBody.Compose(entry));

            EmailSendResult result;
            try
            {
                result = await transport.SendAsync(message, ct);
            }
            catch (Exception ex)
            {
                // トランスポートの例外も**結末として残す**。ここで throw すると、後続の
                // outbox エントリが処理されないまま静かに滞留する。
                logger.LogError(ex, "メール送信に失敗しました。kind={Kind}",
                    Observability.LogSanitizer.Sanitize(entry.Kind));
                result = EmailSendResult.Failure(ex.GetType().Name);
            }

            if (result.Sent)
            {
                entry.MarkSent(now);
                Record(entry, EmailOutboxStatus.Sent, null);
                sentToday++;
                summary.Sent++;
            }
            else
            {
                var reason = result.Reason ?? "unknown";
                entry.Fail(now, reason);
                Record(entry, EmailOutboxStatus.Failed, reason);
                summary.Failed++;
            }
        }

        if (pending.Count > 0)
            await db.SaveChangesAsync(ct);

        return summary;
    }

    // ★ **結末は必ず監査ログとメトリクスの両方へ載せる。**
    // 監査ログは「いつ・誰宛の・どの種別が・どうなったか」を後から追うため、
    // メトリクスは運用ダッシュボード（SC-10）で総量を見るためである。片方だけでは
    // 「静かに落ちない」を満たさない（個別事象と総量は別の問いに答える）。
    private void Record(EmailOutboxEntry entry, string outcome, string? reason)
    {
        metrics.RecordEmailOutcome(entry.Kind, outcome);
        // CodeQL(cs/log-forging): Kind・Subject は受け口（POST /internal/notifications）由来の
        // 外部文字列で、Validate は長さしか見ない。監査ログは最終的に行指向の Console へも
        // 落ちるため、:119 と同じ形でここでもサニタイズする（reason もトランスポート由来の
        // 自由文であり同様に落とす。波 2 レビュー指摘の回収）。
        audit.Record(AuditActionPrefix + outcome,
            Observability.LogSanitizer.Sanitize(entry.Subject), outcome,
            reason is null
                ? $"kind={Observability.LogSanitizer.Sanitize(entry.Kind)}"
                : $"kind={Observability.LogSanitizer.Sanitize(entry.Kind)} "
                    + $"reason={Observability.LogSanitizer.Sanitize(reason)}");
    }
}

// 送出 1 回分の結末の内訳。
public sealed class DispatchSummary
{
    public int Sent { get; set; }
    public int Deferred { get; set; }
    public int Dropped { get; set; }
    public int Failed { get; set; }
}
