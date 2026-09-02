using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02: 起動時に Qdrant コレクション（検索インデックス）の存在を保証する。
// コレクションが未作成だとチャンク登録（upsert）が失敗するため、取り込み開始前に作成する。
public class QdrantBootstrapHostedService(
    IServiceProvider services,
    ILogger<QdrantBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIngestionVectorStore>();
        try
        {
            // ADR-0016: モデル別コレクション（voyage/1024・ruri/768）を実次元で作成・保証する。
            // FR-03, #1116: 併せて `text` の全文ペイロードインデックスを（新規・既存とも）張る。
            await store.EnsureCollectionsAsync(ct);
            // FR-03, #1118, [[IADR-0339]] 決定 2: 日本語 2-gram（`text_ngram`）の索引も同じ作法で張る。
            // 既存の点への後付けは `QdrantCjkNgramBackfillHostedService` が起動後に行う（ここで待たない）。
            await store.EnsureCjkNgramIndexAsync(ct);
            logger.LogInformation(
                "Qdrant collections and full-text payload indexes (text, text_ngram) ensured for ingestion index (per-model)");
        }
        catch (Exception ex)
        {
            // 起動を止めない。Qdrant が一時的に未起動でも、初回 upsert 前に再試行余地を残す。
            //
            // 🔴 FR-03, #1116: **索引の失敗は upsert の失敗と違って自分では現れない。**
            // コレクションが無ければ次の upsert が例外になるが、全文インデックスが無くても
            // 検索は 200 を返し続ける（Qdrant v1.18.1 は部分文字列の全走査へ黙って落ちる）。
            // ここが唯一の記録になるので **Error で残す**。運用の検出は検索側の readiness
            // （RetrievalService の `qdrant-fulltext-index`）が受け持つ（[[IADR-0318]] 決定 3）。
            logger.LogError(ex,
                "Failed to ensure Qdrant collection / full-text payload index at startup; "
                + "keyword search will silently degrade until this succeeds");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
