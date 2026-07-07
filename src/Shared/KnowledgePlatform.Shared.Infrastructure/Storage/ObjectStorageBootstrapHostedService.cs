using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KnowledgePlatform.Shared.Infrastructure.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015: 起動時にオブジェクトストレージのバケット存在とバージョニングを保証する。
// 書き込み側（ConversionService）で登録する。実クライアント（S3ObjectStorageClient）未構成のときは何もしない。
// QdrantBootstrapHostedService（IngestionService）と同じ「起動時にストアを整える」方針。
public sealed class ObjectStorageBootstrapHostedService(
    IObjectStorageClient client,
    ObjectStorageOptions options,
    ILogger<ObjectStorageBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (client is not S3ObjectStorageClient s3 || !options.EnsureBucketOnStartup)
        {
            logger.LogInformation(
                "Object storage bootstrap skipped (configured={Configured})", options.IsConfigured);
            return;
        }

        try
        {
            await s3.EnsureBucketAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 起動を止めない。MinIO 起動待ち等の一時失敗は保存時に再試行される（MassTransit リトライ）。
            logger.LogWarning(ex, "Object storage bucket bootstrap failed; will rely on retry on first write");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
