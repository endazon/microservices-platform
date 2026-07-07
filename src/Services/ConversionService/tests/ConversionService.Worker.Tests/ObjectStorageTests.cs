using ConversionService.Worker.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Worker.Tests;

// FR-06, FR-12, ADR-0014/ADR-0015, IADR-0024: 参照 URI 変換・縮退・書き込み委譲の単体テスト。
// 実体（MinIO）を要するラウンドトリップは IntegrationTests の ObjectStorageRoundTripTests を参照。
public class ObjectStorageTests
{
    // storage://<bucket>/<key> を組み立て、同じ (bucket, key) へ分解できる（往復）。
    [Fact]
    public void StorageUri_round_trips_bucket_and_key()
    {
        var uri = StorageUri.Build("knowledge-normalized", "abc123/document.md");

        uri.Should().Be("storage://knowledge-normalized/abc123/document.md");
        StorageUri.TryParse(uri, out var bucket, out var key).Should().BeTrue();
        bucket.Should().Be("knowledge-normalized");
        key.Should().Be("abc123/document.md");
    }

    // 先頭スラッシュは正規化し、キー内の階層は保持する。
    [Fact]
    public void StorageUri_normalizes_leading_slash_in_key()
    {
        StorageUri.Build("b", "/nested/key.md").Should().Be("storage://b/nested/key.md");
    }

    // http(s) や空文字は storage:// URI ではない。
    [Theory]
    [InlineData("https://example.com/a.md")]
    [InlineData("storage://bucket")] // key 欠落
    [InlineData("")]
    [InlineData(null)]
    public void StorageUri_rejects_non_storage_uris(string? uri)
    {
        StorageUri.IsStorageUri(uri).Should().BeFalse();
    }

    // エスケープされたキー（日本語・記号）は復元される。
    [Fact]
    public void StorageUri_unescapes_key()
    {
        StorageUri.TryParse("storage://b/%E5%9B%B3/fig%201.png", out _, out var key).Should().BeTrue();
        key.Should().Be("図/fig 1.png");
    }

    // 未配備（Null クライアント）: 保存は決定的 URI を返すが解決不可（読み取りはプレースホルダーへ縮退）。
    [Fact]
    public async Task NullClient_emits_deterministic_uri_but_cannot_resolve()
    {
        var options = new ObjectStorageOptions { Bucket = "knowledge-normalized" };
        var client = new NullObjectStorageClient(options, NullLogger<NullObjectStorageClient>.Instance);

        var uri = await client.PutTextAsync("doc/document.md", "# body", "text/markdown");

        uri.Should().Be("storage://knowledge-normalized/doc/document.md");
        client.CanResolve(uri).Should().BeFalse();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetTextAsync(uri));
    }

    // 書き込み側ポート: StorageObjectStore は共有クライアントへ委譲し、本文は text/markdown で保存する。
    [Fact]
    public async Task StorageObjectStore_delegates_to_client()
    {
        var client = new RecordingClient();
        var store = new StorageObjectStore(client);

        var mdUri = await store.SaveMarkdownAsync("doc/document.md", "# body");
        var assetUri = await store.SaveAssetAsync("doc/assets/fig-1.png", [1, 2, 3], "image/png");

        mdUri.Should().Be("storage://normalized/doc/document.md");
        assetUri.Should().Be("storage://normalized/doc/assets/fig-1.png");
        client.LastTextContentType.Should().StartWith("text/markdown");
        client.LastAssetContentType.Should().Be("image/png");
        client.LastAssetBytes.Should().Equal(1, 2, 3);
    }

    private sealed class RecordingClient : IObjectStorageClient
    {
        public string? LastTextContentType { get; private set; }
        public string? LastAssetContentType { get; private set; }
        public byte[]? LastAssetBytes { get; private set; }

        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
        {
            LastTextContentType = contentType;
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
        {
            LastAssetContentType = contentType;
            LastAssetBytes = bytes;
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public bool CanResolve(string? uri) => StorageUri.IsStorageUri(uri);
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => $"https://signed/{uri}";
    }
}
