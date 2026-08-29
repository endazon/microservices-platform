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
    /// <summary>バケットが存在しないときに S3 が返すエラーコード（#1033 の自己修復の起点）。</summary>
    internal const string NoSuchBucketErrorCode = "NoSuchBucket";

    /// <summary>作成しようとしたバケットが既にある（自分が所有）ときのエラーコード。</summary>
    internal const string BucketAlreadyOwnedByYouErrorCode = "BucketAlreadyOwnedByYou";

    /// <summary>作成しようとしたバケットが既にある（名前が取られている）ときのエラーコード。</summary>
    internal const string BucketAlreadyExistsErrorCode = "BucketAlreadyExists";

    public async Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default)
    {
        var normalizedKey = key.TrimStart('/');
        await PutWithBucketSelfHealAsync(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = normalizedKey,
            ContentBody = text,
            ContentType = contentType
        }, ct), ct);

        var uri = StorageUri.Build(options.Bucket, normalizedKey);
        logger.LogInformation("Stored text object at {Uri} ({Length} chars)", uri, text.Length);
        return uri;
    }

    public async Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default)
    {
        var normalizedKey = key.TrimStart('/');
        using var stream = new MemoryStream(bytes, writable: false);
        await PutWithBucketSelfHealAsync(() =>
        {
            stream.Position = 0;
            return s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = normalizedKey,
                InputStream = stream,
                ContentType = contentType
            }, ct);
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
        if (!exists) await CreateBucketWithVersioningAsync(ct);
        else if (options.EnableVersioning) await PutVersioningAsync(ct);
    }

    // FR-06, FR-12, ADR-0014/ADR-0015, IADR-0303 (#1033): 書き込みの自己修復。
    //
    // 🔴 **バケットを作るのは ConversionService の起動時 bootstrap だけ**であり、その bootstrap は
    // fail-open である（MinIO の起動待ちで例外が出ても警告を出して起動を続ける）。**競合に負けると
    // バケットは作られないまま**になり、以後の書き込みが `NoSuchBucket` で落ち続ける。
    // 実測（develop `3939e72` の integration-stack run 33230268422）: seed の `POST /documents` が
    // `The specified bucket does not exist` で 500 になった。**同じコードで前回の run は緑**であり、
    // 起動順序に依存する競合であることが 2 run の対比で確定している。
    //
    // 🔴 **bootstrap のコメントが約束していた「保存時に再試行される（MassTransit リトライ）」は、
    // この経路では成立していなかった** —— `POST /documents` は同期 HTTP であってメッセージではない。
    // **約束を実装の側で本当にする。**
    //
    // ここに置くのは、書き込み元が 4 サービス 6 箇所に散っているためである（うち 2 サービスは
    // バケットを作らない）。**クライアントに 1 箇所置けば全経路が守られ、起動順序にも依存しない。**
    //
    // 🔴 **`EnsureBucketAsync` は呼ばない。** 同メソッドの存在確認は静的な
    // `AmazonS3Util.DoesS3BucketExistV2Async` であり差し替えられない（＝検査が書けない）。
    // **存在しないことは例外が既に教えている**ので、作成だけを行って 1 度だけ再試行する。
    private async Task PutWithBucketSelfHealAsync(Func<Task> put, CancellationToken ct)
    {
        try
        {
            await put();
        }
        // 🔴 **`NoSuchBucket` だけを捕まえる。** 権限不足・接続不能まで飲み込むと、
        // 「バケットを作れば直る」わけではない失敗を握り潰して原因を隠すことになる。
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchBucketErrorCode)
        {
            logger.LogWarning(
                "Object storage bucket {Bucket} did not exist on write; creating it and retrying once."
                + " 起動時 bootstrap が MinIO の起動待ちに負けた可能性が高い（#1033）。", options.Bucket);
            await CreateBucketWithVersioningAsync(ct);
            await put();
        }
    }

    private async Task CreateBucketWithVersioningAsync(CancellationToken ct)
    {
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
            logger.LogInformation("Created object storage bucket {Bucket}", options.Bucket);
        }
        // 🔴 **自己修復はリクエストごとに走る。** 起動時 bootstrap と違って単一ではないため、
        // バケット未作成の窓へ同時に到達した書き込みが**並行して作成を撃つ**。
        // S3 / MinIO は重複作成を成功にせず `BucketAlreadyOwnedByYou` / `BucketAlreadyExists` を返す
        // （SDK は専用の例外型を持つ。いずれも `AmazonS3Exception` 派生でエラーコードを載せる）。
        //
        // **負けた側にとっても目的は達成されている** —— バケットは在る。ここで投げると
        // 「直っているのに、その書き込みだけが失敗する」ことになり、競合の窓が広いほど
        // 失敗が増える。**作成の意味は「在る状態にする」であって「自分が作る」ではない。**
        catch (AmazonS3Exception ex) when (IsAlreadyPresent(ex))
        {
            logger.LogInformation(
                "Object storage bucket {Bucket} was created concurrently ({ErrorCode}); continuing.",
                options.Bucket, ex.ErrorCode);
        }

        if (options.EnableVersioning) await PutVersioningAsync(ct);
    }

    private static bool IsAlreadyPresent(AmazonS3Exception ex) =>
        ex.ErrorCode is BucketAlreadyOwnedByYouErrorCode or BucketAlreadyExistsErrorCode;

    // ADR-0014, IADR-0008: 冪等な再変換でも履歴を残すためバージョニングを有効化する。
    private Task PutVersioningAsync(CancellationToken ct) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = options.Bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
        }, ct);

    private (string Bucket, string Key) Resolve(string uri)
    {
        if (!StorageUri.TryParse(uri, out var bucket, out var key))
            throw new ArgumentException($"Not a storage:// URI: {uri}", nameof(uri));
        return (bucket, key);
    }
}
