namespace NotificationService.Features.Notifications;

// FR-22, ADR-0045 決定 1・1-b, IADR-0215 決定 3: メール送出のトランスポート。
//
// 経路は **Google のメールサーバへの SMTP リレー**であり、第三者の配信サービスは使わない。
// **実体はこの環境に無い**（IADR-0197 決定 5 / 利用者裁定: 実環境が要るものは触らない）ので、
// port だけを置いて差し替え可能にする。
public interface IEmailTransport
{
    // 送信できたら true。**送信できなかったときは例外か false のどちらかで必ず知らせる。**
    // 「設定が無いので何もしないで成功を返す」は禁じる —— それが受け入れ基準 5 の
    // 「静かに落ちる」そのものだからである。
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

// 送出するメールの材料。**本文の文字列そのものは持たない** —— 種別と数値だけを渡し、
// 文面はトランスポートの手前で組み立てる（NotificationMailBody）。
public sealed record EmailMessage(
    string Subject,
    string Kind,
    int? Count,
    int? ThresholdPercent,
    DateTimeOffset? Deadline,
    string? RecipientAddress,
    string Body);

// 失敗理由は**機械的な理由語**に限る（利用者の資料に由来する文字列を混ぜない）。
public sealed record EmailSendResult(bool Sent, string? Reason = null)
{
    public static EmailSendResult Success() => new(true);
    public static EmailSendResult Failure(string reason) => new(false, reason);
}
