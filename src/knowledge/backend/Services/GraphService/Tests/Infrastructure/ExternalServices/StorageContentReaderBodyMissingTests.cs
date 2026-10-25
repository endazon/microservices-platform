using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using GraphService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-17, [[IADR-0549]] 決定 4 (#1931 項目 3): グラフの本文の読み手は、本文の NoSuchKey を「本文が無い」（null）として返す。
// 🔴 キーの欠落だけを null にし、バケットが無い・5xx は例外のまま上げる（再試行へ委ねる）。
public class StorageContentReaderBodyMissingTests
{
    private const string Uri = "storage://knowledge-normalized/documents/abc/body.md";

    private sealed class ThrowingStorage(Exception failure) : IObjectStorageClient
    {
        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) => throw failure;
        public bool CanResolve(string? uri) => true;
        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotImplementedException();
    }

    private static StorageContentReader Sut(Exception failure) =>
        new(new HttpClient(), new ThrowingStorage(failure), NullLogger<StorageContentReader>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 本文のキーが無ければnullを返し_再試行へ回さない()
    {
        (await Sut(new NoSuchKeyException("simulated")).ReadAsync(Uri, Ct)).Should().BeNull();
        (await Sut(new AmazonS3Exception("simulated") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" })
            .ReadAsync(Uri, Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    public async Task バケットが無い_5xxは例外のまま上げる(HttpStatusCode status, string code)
    {
        var act = () => Sut(new AmazonS3Exception("simulated") { StatusCode = status, ErrorCode = code }).ReadAsync(Uri, Ct);

        await act.Should().ThrowAsync<AmazonS3Exception>();
    }
}
