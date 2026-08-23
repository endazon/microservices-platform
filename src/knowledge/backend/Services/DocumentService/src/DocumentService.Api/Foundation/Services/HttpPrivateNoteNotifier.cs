using System.Net.Http.Json;
using DocumentService.Api.Foundation.Ports;

namespace DocumentService.Api.Foundation.Services;

// FR-22, IADR-0215, IADR-0267, [[IADR-0270]] 決定 6: NotificationService への送出アダプタ。
//
// 🔴 **受け口（NotificationService の POST /internal/notifications）は未実装である**（platform 側・
// 統括へ依頼済み）。受け口が入るまで送出は失敗し続けるが、**失敗はエラーログへ記録して
// 握り潰さない。ただし本体操作（同期・削除・保存）の成否には影響させない** —— 通知は
// 本体操作の従属物ではない（IADR-0215 決定 3 が定めた「従属させない」の発火側の対）。
//
// ペイロードは NotificationPublisher.PublishAsync と同形（subject / kind / occurredAt / count /
// thresholdPercent / deadline）。**自由文フィールドは無い**（FR-22 受け入れ基準）。
public sealed class HttpPrivateNoteNotifier(
    IHttpClientFactory httpFactory,
    ILogger<HttpPrivateNoteNotifier> logger) : IPrivateNoteNotifier
{
    public const string ClientName = "NotificationService";
    public const string IngressPath = "/internal/notifications";

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

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogError(
                    "通知の送出に失敗しました（status={Status}）。kind={Kind} subject は本文へ出さない。",
                    (int)resp.StatusCode, kind);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "通知の送出に失敗しました（通信エラー）。kind={Kind}", kind);
        }
    }
}
