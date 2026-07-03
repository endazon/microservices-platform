namespace ConversionService.Worker.Services;

// FR-12, ADR-0014: 正規化本文・資産をオブジェクトストレージ（S3 互換）へ保管する。
// 実運用では MinIO/S3 クライアントでアップロードする。オブジェクトストレージ未配備の dev 環境では
// 決定的な参照 URI を生成してデグレードする（IngestionService/PandocConversionService と同じ方針）。
public class StorageObjectStore(IConfiguration config, ILogger<StorageObjectStore> logger) : IObjectStore
{
    // 参照 URI のベース（オブジェクトキー空間）。ADR-0014 の「メタデータ＋参照」方式の参照側。
    private readonly string _baseUri =
        (config["Storage:NormalizedBaseUri"] ?? "storage://knowledge/normalized").TrimEnd('/');

    public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default)
    {
        var uri = $"{_baseUri}/{key.TrimStart('/')}";
        // 実運用: PutObject(bucket, key, markdown, "text/markdown")。dev は URI のみ発行。
        logger.LogInformation("Storing normalized markdown at {Uri} ({Length} chars)", uri, markdown.Length);
        return Task.FromResult(uri);
    }

    public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default)
    {
        var uri = $"{_baseUri}/{key.TrimStart('/')}";
        // 実運用: PutObject(bucket, key, bytes, contentType)。dev は URI のみ発行。
        logger.LogInformation("Storing asset at {Uri} ({Bytes} bytes, {ContentType})",
            uri, bytes.Length, contentType);
        return Task.FromResult(uri);
    }
}
