using Microsoft.Extensions.Logging;

namespace KnowledgePlatform.Shared.Infrastructure.Storage;

// FR-06, FR-12, ADR-0014: オブジェクトストレージ未配備（Endpoint 未設定）の dev/test 環境向け縮退実装。
// 保存は決定的な参照 URI を発行するのみで実体は永続化しない（従来の開発用スタブと同等）。
// CanResolve は false を返し、読み取り側はプレースホルダー本文へグレースフルデグレードする。
public sealed class NullObjectStorageClient(
    ObjectStorageOptions options,
    ILogger<NullObjectStorageClient> logger) : IObjectStorageClient
{
    public Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default)
    {
        var uri = StorageUri.Build(options.Bucket, key);
        logger.LogWarning(
            "Object storage not configured; emitting deterministic URI {Uri} without persisting ({Length} chars)",
            uri, text.Length);
        return Task.FromResult(uri);
    }

    public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default)
    {
        var uri = StorageUri.Build(options.Bucket, key);
        logger.LogWarning(
            "Object storage not configured; emitting deterministic URI {Uri} without persisting ({Bytes} bytes)",
            uri, bytes.Length);
        return Task.FromResult(uri);
    }

    public Task<string> GetTextAsync(string uri, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Object storage is not configured; cannot fetch " + uri);

    public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Object storage is not configured; cannot fetch " + uri);

    // 未配備のため解決不可（読み取り側はプレースホルダーへ縮退する）。
    public bool CanResolve(string? uri) => false;

    public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) =>
        throw new NotSupportedException(
            "Object storage is not configured; cannot presign " + uri);
}
