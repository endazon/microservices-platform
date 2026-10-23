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
//
// FR-22, IADR-0600 決定 1: **Transient は「トランスポートが一過性と判別できた失敗」だけに立てる**
// （接続断・時間切れ・一時的な SMTP 応答＝4xx 系）。判別できない失敗・恒久的な失敗（宛先不正・
// 未構成）は既定の false のままで、従来どおり即 `failed` になる —— **迷ったら恒久側へ倒す**
// （未構成を一過性と扱うと、毎周期の再送が止まらない）。
public sealed record EmailSendResult(bool Sent, string? Reason = null, bool Transient = false)
{
    public static EmailSendResult Success() => new(true);
    public static EmailSendResult Failure(string reason) => new(false, reason);
    public static EmailSendResult TransientFailure(string reason) => new(false, reason, Transient: true);
}
