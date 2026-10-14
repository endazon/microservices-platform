using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Features.Ingestion.ReembedPending;

// FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 2・3 (#1908): **埋め込み待ちの点を数え、埋め直す常駐の器。**
//
// 起動直後に 1 周期を走らせ、以後 `EmbeddingPendingOptions.Interval`（既定 300 秒）ごとに繰り返す。
// 鍵を入れた後は、取り込みの Pod を作り直さなくても次の周期で埋め直しが始まる（作り直せば起動直後に始まる）。
//
// 🔴 **例外はここで必ず捕まえる**（`QdrantCjkNgramBackfillHostedService` と同じ理由。未捕捉はホストを止める）。
// 1 周期の失敗（Qdrant・ゲートウェイの不達・時間切れ）は Warning で残して次の周期へ進む。
// 🔴 **ブートストラップ（`QdrantBootstrapHostedService`）より後に登録する**（語彙索引のコレクションを作ってから数える）。
public sealed class EmbeddingPendingReembedHostedService(
    EmbeddingPendingReembedder reembedder,
    EmbeddingPendingOptions options,
    TimeProvider clock,
    ILogger<EmbeddingPendingReembedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reembedder.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Embedding-pending re-embed cycle failed; points awaiting embedding stay in the lexical index "
                    + "(still found by keyword/hybrid search) until a later cycle succeeds (IADR-0536)");
            }

            try
            {
                await Task.Delay(options.Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
