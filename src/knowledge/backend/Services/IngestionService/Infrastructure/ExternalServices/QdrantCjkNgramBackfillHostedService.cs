using IngestionService.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-03, #1118, [[IADR-0331]] 決定 2: **既に索引されている点へ、日本語 2-gram ペイロード `text_ngram` を後付けする。**
//
// `text_ngram` の索引は `QdrantBootstrapHostedService` が起動時に張るが、索引があっても**点がペイロードを
// 持たなければ日本語は 0 件のまま**である。再取り込み（DocumentUpdated の再発行）を運用に要求せず、
// 起動後にバックグラウンドで「無い点だけ」を埋める。2 回目以降の起動では 0 件走査で終わる。
//
// **起動を塞がない**（`BackgroundService`）。NFR-08 の規模（数十万点）では一度きりの走査が分単位になり得るが、
// その間も取り込みと検索は動く（新しい点は `UpsertChunkAsync` が最初から `text_ngram` を書く）。
//
// 🔴 **例外はここで必ず捕まえる。** `BackgroundService.ExecuteAsync` の未捕捉例外は既定でホストを止める
// （`BackgroundServiceExceptionBehavior.StopHost`）。後付けの失敗で取り込みサービス全体を落とすのは、
// 索引の欠落で検索を落とさない（NFR-06）のと同じ理由で採らない。**Error で残す**（取り込み側にしか痕跡が出ない）。
public sealed class QdrantCjkNgramBackfillHostedService(
    IServiceProvider services,
    ILogger<QdrantCjkNgramBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIngestionVectorStore>();
        try
        {
            var filled = await store.BackfillCjkNgramAsync(stoppingToken);
            logger.LogInformation(
                "Backfilled CJK 2-gram payload (text_ngram) on {Count} existing point(s); "
                + "Japanese keyword search covers pre-existing chunks from now on", filled);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求。次の起動で続きから（無い点だけ）埋める。
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to backfill CJK 2-gram payload (text_ngram); Japanese keyword search will miss "
                + "chunks indexed before this version until the backfill succeeds on a later start");
        }
    }
}
