using IngestionService.Worker.Foundation.Ports;
using IngestionService.Worker.Foundation.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Composable.Adapters;

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
            await store.EnsureCollectionsAsync(ct);
            logger.LogInformation("Qdrant collections ensured for ingestion index (per-model)");
        }
        catch (Exception ex)
        {
            // 起動を止めない。Qdrant が一時的に未起動でも、初回 upsert 前に再試行余地を残す。
            logger.LogWarning(ex, "Failed to ensure Qdrant collection at startup; will rely on retry");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
