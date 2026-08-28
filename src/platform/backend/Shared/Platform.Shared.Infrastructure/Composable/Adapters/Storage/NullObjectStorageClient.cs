using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Composable.Adapters.Storage;

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

    // FR-06, FR-19, ADR-0057 決定 1, IADR-0296: **`Put*` と同じ作法（警告して成功）にする。**
    //
    // 🔴 **例外にしてはならない。** ストレージ未構成の dev/test 環境では本文がそもそも永続化されて
    // おらず（`Put*` は URI を返すだけ）、**消すべき実体が存在しない**。ここで
    // `NotSupportedException` を投げると、個人資料の完全削除（FR-19）と文書削除（FR-06）が
    // 未構成環境で 500 になる —— **消えていないのではなく、最初から書かれていない**のだから、
    // 削除としては成功で正しい。`Get*` が例外なのは「無い本文を返せない」からであって、向きが逆である。
    public Task DeleteAsync(string uri, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Object storage not configured; treating delete of {Uri} as a no-op (nothing was persisted)",
            uri);
        return Task.CompletedTask;
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
