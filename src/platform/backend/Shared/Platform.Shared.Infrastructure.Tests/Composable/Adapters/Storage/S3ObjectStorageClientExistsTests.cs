using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, [[IADR-0549]] 決定 3 (#1931 項目 3): オブジェクトの存在確認（`ExistsAsync`）を実 I/O 無しで固定する。
//
// 🔴 **「無い」と答えるのはキーが無い（NoSuchKey）ときだけ**である。HEAD の 404 だけでは答えない
// （HEAD の応答は本文を持たず、バケットが無いときも同じ 404 になる）。在るときは HEAD だけで終わり、本文を読まない。
//
// 器は既存試験と同じく `AmazonS3Client` の派生（`GetObjectMetadataAsync` / `GetObjectAsync` は `public virtual`）。
public class S3ObjectStorageClientExistsTests
{
    private const string Uri = "storage://knowledge-normalized/documents/abc/body.md";

    private sealed class FakeS3(Func<Exception?> headOutcome, Func<Exception?> getOutcome)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        public List<GetObjectMetadataRequest> HeadCalls { get; } = [];
        public List<GetObjectRequest> GetCalls { get; } = [];

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            HeadCalls.Add(request);
            if (headOutcome() is { } failure) throw failure;
            return Task.FromResult(new GetObjectMetadataResponse { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<GetObjectResponse> GetObjectAsync(
            GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            GetCalls.Add(request);
            if (getOutcome() is { } failure) throw failure;
            return Task.FromResult(new GetObjectResponse
            {
                HttpStatusCode = HttpStatusCode.PartialContent,
                ResponseStream = new MemoryStream([0x23]),
            });
        }
    }

    private static AmazonS3Exception S3Error(HttpStatusCode status, string errorCode) =>
        new("simulated") { StatusCode = status, ErrorCode = errorCode };

    private static S3ObjectStorageClient Sut(IAmazonS3 s3) =>
        new(s3, new ObjectStorageOptions { Bucket = "knowledge-normalized" }, NullLogger<S3ObjectStorageClient>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 在るときはHEADだけで在ると答え_本文のGETを撃たない()
    {
        var s3 = new FakeS3(() => null, () => throw new InvalidOperationException("GET は撃たない"));

        (await Sut(s3).ExistsAsync(Uri, Ct)).Should().BeTrue();

        s3.HeadCalls.Should().ContainSingle();
        s3.HeadCalls[0].BucketName.Should().Be("knowledge-normalized");
        s3.HeadCalls[0].Key.Should().Be("documents/abc/body.md");
        s3.GetCalls.Should().BeEmpty("在る文書の本文を読み出さない");
    }

    [Fact]
    public async Task HEADが404で_範囲GETがNoSuchKeyなら無いと答える()
    {
        // S3 互換の実装が汎用の `AmazonS3Exception` にエラーコードだけを載せて返す形。
        var s3 = new FakeS3(() => S3Error(HttpStatusCode.NotFound, "NotFound"), () => S3Error(HttpStatusCode.NotFound, "NoSuchKey"));

        (await Sut(s3).ExistsAsync(Uri, Ct)).Should().BeFalse();

        s3.GetCalls.Should().ContainSingle();
        s3.GetCalls[0].ByteRange.Start.Should().Be(0);
        s3.GetCalls[0].ByteRange.End.Should().Be(0, "確かめの GET は 1 バイトまでしか求めない");
    }

    [Fact]
    public async Task 範囲GETがSDKの専用の型NoSuchKeyExceptionでも無いと答える()
    {
        var s3 = new FakeS3(() => S3Error(HttpStatusCode.NotFound, "NotFound"),
            () => new NoSuchKeyException("simulated") { StatusCode = HttpStatusCode.NotFound });

        (await Sut(s3).ExistsAsync(Uri, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task HEADが404でもバケットが無いNoSuchBucketなら無いと言わずに投げる()
    {
        // 🔴 404 だけで「無い」と答えると、バケットの名前の誤り 1 つで全件がアーカイブされる。
        var s3 = new FakeS3(() => S3Error(HttpStatusCode.NotFound, "NotFound"),
            () => S3Error(HttpStatusCode.NotFound, "NoSuchBucket"));

        var act = () => Sut(s3).ExistsAsync(Uri, Ct);

        (await act.Should().ThrowAsync<AmazonS3Exception>()).Which.ErrorCode.Should().Be("NoSuchBucket");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError")]
    public async Task HEADが404以外で失敗したら投げ_GETで確かめ直さない(HttpStatusCode status, string code)
    {
        var s3 = new FakeS3(() => S3Error(status, code), () => throw new InvalidOperationException("GET は撃たない"));

        var act = () => Sut(s3).ExistsAsync(Uri, Ct);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        s3.GetCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HEADとGETの間に作られていれば在ると答える()
    {
        var s3 = new FakeS3(() => S3Error(HttpStatusCode.NotFound, "NotFound"), () => null);

        (await Sut(s3).ExistsAsync(Uri, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task 縮退クライアントは在るとも無いとも答えずに投げる()
    {
        // 🔴 false を返すと、未構成の環境で全件が「本文なし」と数えられ、アーカイブされる。
        var sut = new NullObjectStorageClient(new ObjectStorageOptions(), NullLogger<NullObjectStorageClient>.Instance);

        var act = () => sut.ExistsAsync(Uri, Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task 存在確認を実装しない偽物は既定で投げる()
    {
        IObjectStorageClient fake = new NoProbeStorage();

        var act = () => fake.ExistsAsync(Uri, Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class NoProbeStorage : IObjectStorageClient
    {
        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetTextAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException();
        public bool CanResolve(string? uri) => true;
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotImplementedException();
    }
}
