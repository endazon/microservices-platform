using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Composable.Adapters.Storage;

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
            // 起動を止めない（MinIO の起動待ちで全サービスが落ちるのは割に合わない）。
            //
            // 🔴 **［#1033］従前ここは「保存時に再試行される（MassTransit リトライ）」と書いていた。
            // それは誤りだった。** 書き込み元 4 サービスのうち DocumentService の `POST /documents` と
            // DataSourceService の同期経路は**メッセージではなく同期 HTTP** であり、
            // **その経路に再試行は無かった**。結果、ここで握り潰すとバケットが作られないまま
            // 書き込みが `NoSuchBucket` で 500 になり、そのスタックは以後ずっと壊れたままになった
            // （develop `3939e72` の integration-stack run 33230268422 で実測）。
            //
            // **いまは約束が本当になっている** —— `S3ObjectStorageClient` の書き込みが
            // `NoSuchBucket` を捕まえてバケットを作り、1 度だけ再試行する（IADR-0303）。
            // **fail-open のままでよいのは、その自己修復があるからである。**
            // 自己修復を外すなら、ここも fail-closed へ変えなければならない。
            logger.LogWarning(
                ex,
                "Object storage bucket bootstrap failed; first write will create the bucket and retry (#1033)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
