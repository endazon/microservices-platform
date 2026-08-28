namespace NotificationService.Features.Notifications;

// FR-22, ADR-0045, IADR-0215 決定 3: SMTP が未設定のときの既定トランスポート。
//
// ★ **「送信しない」ではなく「送信できなかった」を返す。**
// 未設定を成功として扱うと、送れていない事実がどこにも残らない —— それが
// 計画 FR-22 の受け入れ基準「送信上限を超える通知が静かに落ちない」が禁じている形である。
// 本実装が返す failure は outbox の `failed` になり、監査ログとメトリクスに載る。
public sealed class UnconfiguredSmtpEmailTransport : IEmailTransport
{
    public const string Reason = "smtp-not-configured";

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        => Task.FromResult(EmailSendResult.Failure(Reason));
}
