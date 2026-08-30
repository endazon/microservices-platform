using ConversionService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0014/ADR-0015, IADR-0008/IADR-0024: 正規化本文・資産を S3 互換オブジェクトストレージ
// （MinIO）へ保管する。共有クライアント（IObjectStorageClient）へ委譲し、キー設計・冪等 ID の方針は
// NormalizationService（呼び出し側）が決める。ストレージ未配備の dev では縮退クライアントが
// 決定的な参照 URI を発行する（NullObjectStorageClient）。
public class StorageObjectStore(IObjectStorageClient storage) : IObjectStore
{
    public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default) =>
        storage.PutTextAsync(key, markdown, "text/markdown; charset=utf-8", ct);

    public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default) =>
        storage.PutBytesAsync(key, bytes, contentType, ct);

    // UC-06: 解決できない参照（ストレージ未配備の dev の縮退クライアント等）は null を返す。
    // **例外にしない**——人手補正の可否は「本文を読めたか」で判断し、呼び出し側が 409 へ写像する。
    public async Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default)
    {
        if (!storage.CanResolve(uri)) return null;
        return await storage.GetTextAsync(uri, ct);
    }
}
