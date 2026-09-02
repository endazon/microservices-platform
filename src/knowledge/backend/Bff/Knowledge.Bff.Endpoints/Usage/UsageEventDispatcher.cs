using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints.Usage;

// FR-10, SC-10, ADR-0002, ADR-0006, [[IADR-0343]] 決定 2・3・4 (#1103):
// 送出待ちの列を排出し、DashboardService の受け口へ POST する常駐処理。
//
// **送出は HTTP である。メッセージングは選べない**（[[IADR-0343]] 決定 2）——
// 受け口 `POST /dashboard/events` は `RequireAuthorization()` を持ち、**利用者主体を
// `HttpContext.User` から解決する**。ブローカ経由にすると受け手は本文の自己申告 userId を
// 信じるほかなく、認証済みの主体が自己申告に置き換わる（認可の後退）。
//
// 🔴 **fail-open である。** 停止要求以外はすべて握り、計器とエラーログへ落とす
// （`HttpPrivateNoteNotifier` / `HttpKnowledgeHealthReporter` と同じ姿勢・同じ理由）。
// ここで例外を投げるとホスト全体が落ち、**計測のために検索と回答が止まる**。
//
// ★ **タイムアウトは既定の 100 秒ではなく SendTimeout（5 秒）である。**
// 受け口が応答しないとき、既定では 1 件が 100 秒かけて列を占有し、後続がすべて溢れる。
// 名前付きクライアント側の設定は変えない —— 同じクライアントを `/bff/dashboard/summary` の
// 集約が共有しており、そちらの上限まで動かしてしまうためである。
public sealed class UsageEventDispatcher(
    UsageEventQueue queue,
    IHttpClientFactory httpFactory,
    UsageEventMetrics metrics,
    ILogger<UsageEventDispatcher> logger) : BackgroundService
{
    // Program.cs が登録済みの名前付きクライアント（`/bff/dashboard/summary` と同じもの）。
    public const string ClientName = "DashboardService";

    // 🔴 受け口 DashboardService.Features.Dashboard.RecordEvent（`/dashboard` グループ ＋ `/events`）と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。
    // `/internal/notifications` と同じく、**文字列の一致は両側のテストで固定している**。
    public const string EventsPath = "/dashboard/events";

    // 送出 1 回あたりの上限。**列を占有させないための値**である。
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var signal in queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(signal, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求。**列に残ったぶんは捨てる** —— 計測は best-effort であり、
            // 送り切るために停止を遅らせない（利用状況の 1 件は再送に値しない）。
        }
    }

    private async Task SendAsync(UsageEventSignal signal, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SendTimeout);

        try
        {
            var client = httpFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, EventsPath)
            {
                // **送るのは種別と検索語だけである**（決定 5）。利用者は受け口が JWT から解決する。
                Content = JsonContent.Create(new UsageEventRequest(signal.EventType, signal.Query)),
            };
            // 受け口は認証必須である。**利用者の資格情報をそのまま運ぶ** —— これが
            // 「誰の利用か」を後段が解決する唯一の材料である（発火点を BFF に置いた理由）。
            if (!string.IsNullOrEmpty(signal.Authorization))
                request.Headers.TryAddWithoutValidation("Authorization", signal.Authorization);

            using var response = await client.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                metrics.RecordDispatch(signal.EventType, UsageEventMetrics.OutcomeSent);
                return;
            }

            metrics.RecordDispatch(signal.EventType, UsageEventMetrics.OutcomeRejected);
            logger.LogError(
                "利用状況イベントの送出に失敗した（status={Status}）。eventType={EventType}。"
                + "検索語と利用者は本文へ出さない。",
                (int)response.StatusCode, signal.EventType);
        }
        // ★ 停止要求以外は**すべて**握る（fail-open）。when 節に合致しない例外（＝停止要求）は
        // そのまま伝播し、上の ExecuteAsync が正常終了として扱う。
        catch (Exception ex) when (!IsShutdown(ex, stoppingToken))
        {
            metrics.RecordDispatch(signal.EventType, UsageEventMetrics.OutcomeUnreachable);
            logger.LogError(ex,
                "利用状況イベントの送出に失敗した（受け口へ到達できない）。eventType={EventType}。"
                + "検索語と利用者は本文へ出さない。",
                signal.EventType);
        }
    }

    // シャットダウンはホストの事情であり、送出の失敗ではない。
    // **5 秒のタイムアウトは失敗である**（stoppingToken は立っていない）ので、ここには入らない。
    private static bool IsShutdown(Exception ex, CancellationToken stoppingToken)
        => ex is OperationCanceledException && stoppingToken.IsCancellationRequested;
}
