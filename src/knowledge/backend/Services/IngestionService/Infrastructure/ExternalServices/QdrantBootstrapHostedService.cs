using IngestionService.Common.Concurrency;
using IngestionService.Common.Observability;
using IngestionService.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02: 起動時に Qdrant コレクション（検索インデックス）の存在を保証する。
// コレクションが未作成だとチャンク登録（upsert）が失敗するため、取り込み開始前に作成する。
//
// 🔴 FR-02, FR-03, [[IADR-0650]] (#2283): **成功するまで、上限つきのバックオフでバックグラウンドで再試行する。**
// 従前は起動時に 1 回だけ試し、失敗を Error のログに残して終わっていた。コメントは「初回 upsert 前に再試行余地を残す」と
// 書いていたが、upsert の経路は保証を呼ばないので、**1 回の失敗で Pod が生きている間ずっと書き込みが失敗した**
// （MSP#2281 の e2e-live。CoreDNS の再起動と重なった一過性の失敗とみられる）。
// - 起動は塞がない（`BackgroundService`）。成功するまで readiness は NotReady（`QdrantBootstrapHealthCheck`）。
// - 失敗は計器（`ingestion.qdrant_bootstrap.failures`）で数え、ログは最初の失敗を Error・以後を Warning で残す。
// - 待ちは 1 秒から倍々で、上限 30 秒（`Backoff`）。回数の上限は置かない（諦めると NotReady のまま止まるだけで、得るものが無い）。
public sealed class QdrantBootstrapHostedService(
    IServiceProvider services,
    ILogger<QdrantBootstrapHostedService> logger,
    QdrantBootstrapState state,
    QdrantBootstrapMetrics metrics,
    QdrantBootstrapHostedService.Backoff? backoff = null) : BackgroundService
{
    /// <summary>再試行の待ち（初回の待ちと上限）。試験は短くする。</summary>
    public sealed record Backoff(TimeSpan Initial, TimeSpan Max)
    {
        public static readonly Backoff Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        public TimeSpan After(int failures) =>
            TimeSpan.FromTicks(Math.Min(Max.Ticks, Initial.Ticks * (1L << Math.Min(failures - 1, 20))));
    }

    private readonly Backoff _backoff = backoff ?? Backoff.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (true)
        {
            try
            {
                await EnsureOnceAsync(stoppingToken);
                state.MarkEnsured();
                logger.LogInformation(
                    "Qdrant collections, full-text payload indexes (text, text_ngram) and keyword payload indexes "
                    + "(tags, shared_with, document_id) ensured for ingestion index (per-model and lexical) after {Failures} failure(s)",
                    failures);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                metrics.RecordFailure();
                var wait = _backoff.After(failures);
                // 🔴 FR-03, #1116: **索引の失敗は upsert の失敗と違って自分では現れない**（全文索引が無くても検索は 200 を返す）。
                // 最初の失敗は Error で残す。以後は同じ原因の繰り返しなので Warning にして、回数は計器で読む。
                logger.Log(failures == 1 ? LogLevel.Error : LogLevel.Warning, ex,
                    "Failed to ensure Qdrant collection / payload indexes (attempt {Attempt}); retrying in {Wait}. "
                    + "Ingestion stays NotReady and keyword search degrades until this succeeds",
                    failures, wait);
                try
                {
                    await Task.Delay(wait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task EnsureOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIngestionVectorStore>();
        // ADR-0016: モデル別コレクション（既定は voyage/1024。opt-in のときだけ ruri/768）を実次元で作成・保証する。
        // FR-03, #1116: 併せて `text` の全文ペイロードインデックスを（新規・既存とも）張る。
        await store.EnsureCollectionsAsync(ct);
        // FR-03, #1118, [[IADR-0339]] 決定 2: 日本語 2-gram（`text_ngram`）の索引も同じ作法で張る。
        // 既存の点への後付けは `QdrantCjkNgramBackfillHostedService` が保証の成功の後に行う。
        await store.EnsureCjkNgramIndexAsync(ct);
        // FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 2 (i) (#1760): facet と ABAC フィルタが引く集合値キー
        // （`tags`・`shared_with`）のキーワード索引も同じ作法で張る。属性キーは書き込み時と
        // `QdrantKeywordIndexDiscoveryHostedService`（既存の点からの発見）が張る。
        await store.EnsureKeywordIndexesAsync(ct);
        // FR-06, SC-05, [[IADR-0565]] 決定 2 (#1993): 索引の状態の照会（facet）が引く `document_id` のキーワード索引も張る。
        await store.EnsureDocumentIdIndexAsync(ct);
    }
}
