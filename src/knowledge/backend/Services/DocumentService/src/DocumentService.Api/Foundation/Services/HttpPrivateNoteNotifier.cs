using System.Net.Http.Json;
using DocumentService.Api.Foundation.Observability;
using DocumentService.Api.Foundation.Ports;

namespace DocumentService.Api.Foundation.Services;

// FR-22, IADR-0215, IADR-0267, [[IADR-0270]] 決定 6: NotificationService への送出アダプタ。
//
// ［2026-08-28 訂正 / #451］受け口（NotificationService の POST /internal/notifications）は実装済み
// である（NotificationIngressEndpoints）。以下の「失敗しても本処理を止めない」設計は、受け口の有無に
// かかわらず配備漏れ・一時障害に対する恒常の姿勢として維持する。送出の失敗はエラーログへ記録して
// 握り潰さない。ただし本体操作（同期・削除・保存）の成否には影響させない** —— 通知は
// 本体操作の従属物ではない（IADR-0215 決定 3 が定めた「従属させない」の発火側の対）。
//
// ★［2026-08-28 追記 / #600］**握る範囲を例外型の列挙から「呼び出し元のキャンセル以外すべて」へ
// 広げた**（IADR-0215 決定 5-b）。従前は HttpRequestException と TaskCanceledException の 2 型だけを
// 握っており、**列挙から漏れた例外**（BaseAddress 不整合の InvalidOperationException・
// シリアライズの JsonException 等）**が呼び出し元へ抜けて業務処理を失敗させ得た**。
// 守りたい性質は「通知の送出は業務処理を失敗させない」であって「HTTP 例外だけを握る」ではない。
// **呼び出し元のキャンセル（シャットダウン・利用者の切断）だけは伝播させる** ——
// 握ると「キャンセルされたのに続行した」ように見える。
//
// ★ **タイムアウトは既定の 100 秒ではなく SendTimeout（5 秒）である**（配線は Program.cs）。
// 受け口が応答しないとき、既定では**利用者の要求がその間止まる**。3 契機はいずれも日・週の粒度で
// あり、5 秒待って届かない通知のために利用者を待たせる理由が無い。
//
// ペイロードは NotificationPublisher.PublishAsync と同形（subject / kind / occurredAt / count /
// thresholdPercent / deadline）。**自由文フィールドは無い**（FR-22 受け入れ基準）。
public sealed class HttpPrivateNoteNotifier(
    IHttpClientFactory httpFactory,
    PrivateNoteNotificationMetrics metrics,
    ILogger<HttpPrivateNoteNotifier> logger) : IPrivateNoteNotifier
{
    public const string ClientName = "NotificationService";
    public const string IngressPath = "/internal/notifications";

    // 送出 1 回あたりの上限。**業務処理を待たせないための値**であり、通知の重要度ではなく
    // 呼び出し元（利用者の要求・定期処理）の応答性から決めている。
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task NotifyAsync(string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient(ClientName);
            var resp = await client.PostAsJsonAsync(IngressPath, new
            {
                subject,
                kind,
                occurredAt,
                count,
                thresholdPercent,
                deadline,
            }, ct);

            if (resp.IsSuccessStatusCode)
            {
                metrics.RecordDispatch(kind, PrivateNoteNotificationMetrics.OutcomeSent);
                return;
            }

            metrics.RecordDispatch(kind, PrivateNoteNotificationMetrics.OutcomeRejected);
            logger.LogError(
                "通知の送出に失敗しました（status={Status}）。kind={Kind} subject は本文へ出さない。",
                (int)resp.StatusCode, kind);
        }
        // ★ 呼び出し元のキャンセル以外は**すべて**握る（fail-open）。when 節に合致しない例外
        // （＝呼び出し元のキャンセル）はそのまま伝播する。
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            metrics.RecordDispatch(kind, PrivateNoteNotificationMetrics.OutcomeUnreachable);
            logger.LogError(ex,
                "通知の送出に失敗しました（受け口へ到達できない）。kind={Kind} subject は本文へ出さない。",
                kind);
        }
    }

    // シャットダウン・利用者の切断は業務処理の側の事情であり、通知の失敗ではない。
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;
}
