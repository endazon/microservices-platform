using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, FR-12, FR-21, ADR-0014/ADR-0015, IADR-0303 (#1033):
// **バケットが無い状態への書き込みが、作って再試行することで通る**ことを実 I/O 無しで固定する。
//
// 🔴 **動機は実測された競合である。** バケットを作るのは ConversionService の起動時 bootstrap だけで、
// その bootstrap は fail-open。MinIO の readiness に負けるとバケットは作られず、以後の書き込みが
// `NoSuchBucket` で 500 になる（develop `3939e72` の integration-stack で実測。前回 run は同じコードで緑）。
//
// 器は `AmazonS3Client` の派生である（既存 `S3ObjectStorageClientDeleteTests` と同じ作法。
// 本テストプロジェクトはモックライブラリを持たない）。`PutObjectAsync` / `PutBucketAsync` /
// `PutBucketVersioningAsync` はいずれも `public virtual` である（SDK 4.0.100.2 で反射により実測）。
// base を一度も呼ばないため**ネットワーク I/O は発生しない**。
public class S3ObjectStorageClientBucketSelfHealTests
{
    private const string Bucket = "test-bucket";

    private sealed class FakeS3(
        int failuresBeforeSuccess,
        string errorCode = "NoSuchBucket",
        string? createBucketErrorCode = null)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        private int _putObjectCalls;

        public int PutObjectCalls => _putObjectCalls;
        public List<PutBucketRequest> BucketCreations { get; } = [];
        public List<PutBucketVersioningRequest> VersioningCalls { get; } = [];

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            _putObjectCalls++;
            if (_putObjectCalls <= failuresBeforeSuccess)
                throw new AmazonS3Exception("boom") { ErrorCode = errorCode };
            return Task.FromResult(new PutObjectResponse());
        }

        public override Task<PutBucketResponse> PutBucketAsync(
            PutBucketRequest request, CancellationToken cancellationToken = default)
        {
            BucketCreations.Add(request);
            if (createBucketErrorCode is not null)
                throw new AmazonS3Exception("already there") { ErrorCode = createBucketErrorCode };
            return Task.FromResult(new PutBucketResponse());
        }

        public override Task<PutBucketVersioningResponse> PutBucketVersioningAsync(
            PutBucketVersioningRequest request, CancellationToken cancellationToken = default)
        {
            VersioningCalls.Add(request);
            return Task.FromResult(new PutBucketVersioningResponse());
        }
    }

    private static S3ObjectStorageClient Sut(IAmazonS3 s3, bool versioning = true) =>
        new(s3, new ObjectStorageOptions { Bucket = Bucket, EnableVersioning = versioning },
            NullLogger<S3ObjectStorageClient>.Instance);

    [Fact]
    public async Task バケットが無ければ作って再試行し書き込みが成功する()
    {
        var s3 = new FakeS3(failuresBeforeSuccess: 1);

        var uri = await Sut(s3).PutTextAsync("documents/a/body.md", "本文", "text/markdown", TestContext.Current.CancellationToken);

        uri.Should().Be(StorageUri.Build(Bucket, "documents/a/body.md"));
        s3.PutObjectCalls.Should().Be(2, "1 度目が NoSuchBucket で落ち、作成後に 1 度だけ再試行する");
        s3.BucketCreations.Should().ContainSingle().Which.BucketName.Should().Be(Bucket);
    }

    [Fact]
    public async Task 自己修復で作ったバケットにもバージョニングを有効化する()
    {
        // ADR-0014 / IADR-0008: 版が残らないバケットを作ってしまうと、
        // 完全削除（全版削除）の前提が静かに崩れる。作成経路が 2 つに増えた以上、両方で有効化する。
        var s3 = new FakeS3(failuresBeforeSuccess: 1);

        await Sut(s3).PutBytesAsync("assets/a/fig.png", [1, 2, 3], "image/png", TestContext.Current.CancellationToken);

        s3.VersioningCalls.Should().ContainSingle()
            .Which.VersioningConfig!.Status.Should().Be(VersionStatus.Enabled);
    }

    [Fact]
    public async Task バケットがあるときは作成を試みない()
    {
        // 無変異のベースライン対照。正常時に余計な PutBucket を撃たないこと。
        var s3 = new FakeS3(failuresBeforeSuccess: 0);

        await Sut(s3).PutTextAsync("documents/a/body.md", "本文", "text/markdown", TestContext.Current.CancellationToken);

        s3.PutObjectCalls.Should().Be(1);
        s3.BucketCreations.Should().BeEmpty();
        s3.VersioningCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task NoSuchBucket以外は握り潰さずそのまま投げる()
    {
        // 🔴 権限不足・接続不能まで飲み込むと、「バケットを作れば直る」わけではない失敗を
        // 隠すことになる。**自己修復の射程を型で固定する。**
        var s3 = new FakeS3(failuresBeforeSuccess: 1, errorCode: "AccessDenied");

        var act = async () => await Sut(s3).PutTextAsync("documents/a/body.md", "本文", "text/markdown", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<AmazonS3Exception>()).Which.ErrorCode.Should().Be("AccessDenied");
        s3.BucketCreations.Should().BeEmpty("作成を試みてはならない");
        s3.PutObjectCalls.Should().Be(1, "再試行してはならない");
    }

    [Theory]
    [InlineData("BucketAlreadyOwnedByYou")]
    [InlineData("BucketAlreadyExists")]
    public async Task 並行作成で負けても書き込みは成功する(string createError)
    {
        // 🔴 **自己修復はリクエストごとに走る**ため、バケット未作成の窓へ同時に到達した書き込みが
        // 並行して作成を撃つ。S3 / MinIO は重複作成を成功にせず「既にある」を返す。
        // **負けた側にとっても目的は達成されている**（バケットは在る）。ここで投げると
        // 「直っているのにその書き込みだけ失敗する」ことになる。
        var s3 = new FakeS3(failuresBeforeSuccess: 1, createBucketErrorCode: createError);

        var uri = await Sut(s3).PutTextAsync(
            "documents/a/body.md", "本文", "text/markdown", TestContext.Current.CancellationToken);

        uri.Should().Be(StorageUri.Build(Bucket, "documents/a/body.md"));
        s3.PutObjectCalls.Should().Be(2, "作成に負けても再試行まで進む");
        s3.VersioningCalls.Should().ContainSingle("既にあっても版の設定は行う");
    }

    [Fact]
    public async Task 再試行は1度だけで2度目のNoSuchBucketは投げる()
    {
        // 無限リトライにしない。作ってもなお NoSuchBucket なら、それは別の問題である。
        var s3 = new FakeS3(failuresBeforeSuccess: 2);

        var act = async () => await Sut(s3).PutTextAsync("documents/a/body.md", "本文", "text/markdown", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        s3.PutObjectCalls.Should().Be(2, "1 度目＋再試行 1 度で打ち止め");
        s3.BucketCreations.Should().ContainSingle();
    }
}
