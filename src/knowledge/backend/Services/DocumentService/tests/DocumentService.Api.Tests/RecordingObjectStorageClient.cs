using System.Collections.Concurrent;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Api.Tests;

// FR-21, ADR-0014/ADR-0015: オブジェクトストレージ（MinIO）の記録用スタブ。
//
// **`NullObjectStorageClient`（本番の縮退実装）では受け入れ基準 ⑦ を検証できない** ——
// あちらは決定的な URI を返すだけで本文を保持しないため、「1 MB 以下の本文が
// **切り詰められることなく**格納された」ことを測る手段が無い。ここでは格納した本文を保持し、
// **入力と 1 バイトも違わないこと**をテストが直接見られるようにする。
public sealed class RecordingObjectStorageClient : IObjectStorageClient
{
    public const string Bucket = "knowledge-normalized";

    // 参照 URI → 格納した本文。
    public ConcurrentDictionary<string, string> Texts { get; } = new();

    // 格納の呼び出し回数。**「拒否したのに保存していた」を検出する**ために数える。
    public int PutTextCallCount;

    public Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref PutTextCallCount);
        var uri = StorageUri.Build(Bucket, key);
        Texts[uri] = text;
        return Task.FromResult(uri);
    }

    public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default) =>
        Task.FromResult(StorageUri.Build(Bucket, key));

    public Task<string> GetTextAsync(string uri, CancellationToken ct = default) =>
        Task.FromResult(Texts.TryGetValue(uri, out var text)
            ? text
            : throw new KeyNotFoundException(uri));

    public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public bool CanResolve(string? uri) => uri is not null && Texts.ContainsKey(uri);

    public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => uri;
}
