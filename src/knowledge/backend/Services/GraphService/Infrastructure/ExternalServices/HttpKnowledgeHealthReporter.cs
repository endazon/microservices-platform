using System.Net.Http.Json;
using GraphService.Domain.Ports;

namespace GraphService.Infrastructure.ExternalServices;

// FR-10, FR-17, SC-10, ADR-0002, ADR-0006, IADR-0265, [[IADR-0299]] (#443):
// DashboardService の観測値受け口への送出アダプタ。
//
// **送出は HTTP である。メッセージングは選べない**（[[IADR-0299]] 決定 2）——
// 受け口の契約は指標 1 つ分の**全量スナップショット置換**であり、順序保証の無い経路で
// 置換を 2 通流すと、**古い方の集合が最終状態として残る**。集合の差し替えは同期呼び出しで行う。
//
// 🔴 **fail-open である。** 呼び出し元のキャンセル以外はすべて握り、エラーログへ落とす
// （`HttpPrivateNoteNotifier` と同じ姿勢・同じ理由）。本サービスは DocumentUpdated /
// DocumentDeleted の購読ホストでもあり、**指標の送出失敗で購読を止めない**。
// 呼び出し元のキャンセル（シャットダウン）だけは伝播させる —— 握ると
// 「キャンセルされたのに続行した」ように見える。
//
// ★ **タイムアウトは既定の 100 秒ではなく SendTimeout（5 秒）である**（配線は Program.cs）。
// 受け口が応答しないとき、既定では定期処理が 100 秒その場で止まる。指標の鮮度は時間粒度であり、
// 5 秒待って届かない報告のために周期を占有する理由が無い。
public sealed class HttpKnowledgeHealthReporter(
    IHttpClientFactory httpFactory,
    ILogger<HttpKnowledgeHealthReporter> logger) : IKnowledgeHealthReporter
{
    public const string ClientName = "DashboardService";

    // 🔴 受け口 DashboardService.Features.KnowledgeHealth.KnowledgeHealthEndpoints.ObservationsPath と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。
    // `/internal/notifications` と同じく、**文字列の一致は両側のテストで固定している**。
    public const string ObservationsPath = "/internal/knowledge-health/observations";

    // 送出 1 回あたりの上限。**周期を占有させないための値**である。
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task ReportAsync(
        string indicator,
        IReadOnlyList<KnowledgeHealthObservation> observations,
        CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient(ClientName);
            var resp = await client.PostAsJsonAsync(ObservationsPath, new
            {
                indicator,
                // **件数ではなく観測値**を送る（受け手が個人資料の除外を強制できるようにするため)。
                observations = observations
                    .Select(o => new { subjectKey = o.SubjectKey, docScope = o.DocScope })
                    .ToList(),
            }, ct);

            if (resp.IsSuccessStatusCode)
                return;

            logger.LogError(
                "ナレッジ健全性の報告に失敗した（status={Status}）。indicator={Indicator} count={Count}。"
                + "対象の識別子は本文へ出さない。",
                (int)resp.StatusCode, indicator, observations.Count);
        }
        // ★ 呼び出し元のキャンセル以外は**すべて**握る（fail-open）。
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            logger.LogError(ex,
                "ナレッジ健全性の報告に失敗した（受け口へ到達できない）。indicator={Indicator} count={Count}。",
                indicator, observations.Count);
        }
    }

    // シャットダウンは呼び出し元の事情であり、報告の失敗ではない。
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;
}
