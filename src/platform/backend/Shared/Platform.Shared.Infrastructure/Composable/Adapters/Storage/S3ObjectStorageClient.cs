using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Composable.Adapters.Storage;

// FR-06, FR-12, ADR-0014/ADR-0015, IADR-0024: MinIO（S3 互換 API）への保存・取得の本実装。
// 参照 URI は storage://<bucket>/<key>。保存は既定バケットへ行い、取得は URI 内のバケットを尊重する。
// バケット・キー設計、バージョニング、アクセス制御方針は .ai-context/adr/IADR-0024 を参照。
public sealed class S3ObjectStorageClient(
    IAmazonS3 s3,
    ObjectStorageOptions options,
    ILogger<S3ObjectStorageClient> logger) : IObjectStorageClient
{
    public async Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default)
    {
        var normalizedKey = key.TrimStart('/');
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = normalizedKey,
            ContentBody = text,
            ContentType = contentType
        }, ct);

        var uri = StorageUri.Build(options.Bucket, normalizedKey);
        logger.LogInformation("Stored text object at {Uri} ({Length} chars)", uri, text.Length);
        return uri;
    }

    public async Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default)
    {
        var normalizedKey = key.TrimStart('/');
        using var stream = new MemoryStream(bytes, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = normalizedKey,
            InputStream = stream,
            ContentType = contentType
        }, ct);

        var uri = StorageUri.Build(options.Bucket, normalizedKey);
        logger.LogInformation("Stored asset object at {Uri} ({Bytes} bytes, {ContentType})",
            uri, bytes.Length, contentType);
        return uri;
    }

    public async Task<string> GetTextAsync(string uri, CancellationToken ct = default)
    {
        var (bucket, key) = Resolve(uri);
        using var response = await s3.GetObjectAsync(bucket, key, ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        logger.LogInformation("Fetched text object from {Uri} ({Length} chars)", uri, content.Length);
        return content;
    }

    public async Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default)
    {
        var (bucket, key) = Resolve(uri);
        using var response = await s3.GetObjectAsync(bucket, key, ct);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    // FR-06, FR-19, ADR-0057 決定 1, IADR-0296: オブジェクトを**全バージョン**削除する。
    //
    // 🔴 **素の DeleteObject では足りない。** バケットのバージョニングは既定で有効
    // （`ObjectStorageOptions.EnableVersioning` / `EnsureBucketAsync`）であり、versionId を伴わない
    // 削除は **delete marker を 1 つ積むだけ**で過去の全版がそのまま残る。ADR-0057 は
    // 「残っていない」ことを受け入れ基準にしているので、版を列挙して 1 つずつ消す。
    //
    // **delete marker も版として消す。** .NET SDK は delete marker を別リストにせず
    // `ListVersionsResponse.Versions` へ混ぜて返す（`S3ObjectVersion.IsDeleteMarker` で区別する。
    // 同応答に `DeleteMarkers` プロパティは存在しない —— SDK 4.0.100.2 で実測）。
    // marker を残すとオブジェクトは「削除済みの版」として残り続ける。
    //
    // **`Prefix` は前方一致なので `Key` の厳密一致で絞る**（`body.md` の削除で `body.md.bak` を巻き込まない）。
    // **`IsTruncated` の間は marker で辿る**（1 応答は既定 1000 件までしか返らない）。
    public async Task DeleteAsync(string uri, CancellationToken ct = default)
    {
        var (bucket, key) = Resolve(uri);

        var removed = 0;
        string? keyMarker = null;
        string? versionIdMarker = null;
        do
        {
            var listed = await s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                Prefix = key,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker
            }, ct);

            foreach (var version in listed.Versions ?? [])
            {
                // Prefix は前方一致。当該キーそのものの版だけを消す。
                if (!string.Equals(version.Key, key, StringComparison.Ordinal)) continue;
                await s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = version.VersionId
                }, ct);
                removed++;
            }

            if (listed.IsTruncated == true)
            {
                keyMarker = listed.NextKeyMarker;
                versionIdMarker = listed.NextVersionIdMarker;
            }
            else
            {
                keyMarker = null;
                versionIdMarker = null;
            }
        }
        while (keyMarker is not null || versionIdMarker is not null);

        // バージョニングが無効なバケット・列挙に現れない未バージョン化オブジェクトの取りこぼしを塞ぐ。
        // versionId 無しの削除は冪等（実在しなくても 204）なので、余分に撃っても害が無い。
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);

        logger.LogInformation("Deleted object {Uri} ({Versions} versions removed)", uri, removed);
    }

    // storage:// スキームなら（実クライアント構成済みのため）解決可能。
    public bool CanResolve(string? uri) => StorageUri.IsStorageUri(uri);

    public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null)
    {
        var (bucket, key) = Resolve(uri);
        // ADR-0014: 直接公開はせず、ABAC 判定を通したサービスが認可済みの呼び出し元にのみ払い出す。
        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromMinutes(options.PresignedUrlExpiryMinutes))
        });
    }

    // 起動時にバケットの存在とバージョニングを保証する（ObjectStorageBootstrapHostedService から呼ぶ）。
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var exists = await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.Bucket);
        if (!exists)
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
            logger.LogInformation("Created object storage bucket {Bucket}", options.Bucket);
        }

        if (options.EnableVersioning)
        {
            // ADR-0014, IADR-0008: 冪等な再変換でも履歴を残すためバージョニングを有効化する。
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = options.Bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
            }, ct);
        }
    }

    private (string Bucket, string Key) Resolve(string uri)
    {
        if (!StorageUri.TryParse(uri, out var bucket, out var key))
            throw new ArgumentException($"Not a storage:// URI: {uri}", nameof(uri));
        return (bucket, key);
    }
}
