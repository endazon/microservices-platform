using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Platform.Shared.Infrastructure.Tests.Testing;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, FR-12, ADR-0014, ADR-0106, IADR-0461 決定 11 (#1562):
// 起動時のバケットの存在確認を **HeadBucket** で行い、答えを「在る／無い／不明」の 3 値で扱うことを実 I/O 無しで固定する。
//
// 🔴 **動機は実測である。** 従前の `AmazonS3Util.DoesS3BucketExistV2Async` は内部で GetBucketAcl を撃ち、
// SeaweedFS 4.47 はバケットが在るときにこれへ 503 を返した。ConversionService の起動のたびに bootstrap の
// 失敗警告が出ていた（書き込み・読み出しは通っていた）。
//
// **不明は無いではない（原則 A）。** 404 / NoSuchBucket だけが作成へ進み、それ以外の失敗では作成を撃たない。
// 本当に無かった場合の回収は書き込み時の自己修復（#1033。`S3ObjectStorageClientBucketSelfHealTests`）が担う。
//
// 器は既存試験と同じく `AmazonS3Client` の派生（`HeadBucketAsync` は `public virtual`）。base を呼ばないため I/O は無い。
// 🔴 旧実装へ戻すと、静的ヘルパは偽物の HeadBucket を通らず 127.0.0.1:1 への実接続に出るので、下の試験は落ちる。
public class S3ObjectStorageClientEnsureBucketTests
{
    private const string Bucket = "test-bucket";

    private sealed class FakeS3(Func<Exception?> headOutcome)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        public List<HeadBucketRequest> HeadCalls { get; } = [];
        public List<PutBucketRequest> BucketCreations { get; } = [];
        public List<PutBucketVersioningRequest> VersioningCalls { get; } = [];

        public override Task<HeadBucketResponse> HeadBucketAsync(
            HeadBucketRequest request, CancellationToken cancellationToken = default)
        {
            HeadCalls.Add(request);
            var failure = headOutcome();
            if (failure is not null) throw failure;
            return Task.FromResult(new HeadBucketResponse { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<PutBucketResponse> PutBucketAsync(
            PutBucketRequest request, CancellationToken cancellationToken = default)
        {
            BucketCreations.Add(request);
            return Task.FromResult(new PutBucketResponse());
        }

        public override Task<PutBucketVersioningResponse> PutBucketVersioningAsync(
            PutBucketVersioningRequest request, CancellationToken cancellationToken = default)
        {
            VersioningCalls.Add(request);
            return Task.FromResult(new PutBucketVersioningResponse());
        }
    }

    private static AmazonS3Exception S3Error(HttpStatusCode status, string errorCode) =>
        new("simulated") { StatusCode = status, ErrorCode = errorCode };

    private static (S3ObjectStorageClient Sut, RecordingLogger<S3ObjectStorageClient> Log) Sut(
        IAmazonS3 s3, bool versioning = true)
    {
        var log = new RecordingLogger<S3ObjectStorageClient>();
        return (new S3ObjectStorageClient(
            s3, new ObjectStorageOptions { Bucket = Bucket, EnableVersioning = versioning }, log), log);
    }

    [Fact]
    public async Task 在るとき_HeadBucketで確かめ_作成せず版管理だけ有効化し_警告を出さない()
    {
        // #1562 の本体。SeaweedFS で在るバケットに対して起動のたびに警告が出ていた経路。
        var s3 = new FakeS3(() => null);
        var (sut, log) = Sut(s3);

        await sut.EnsureBucketAsync(TestContext.Current.CancellationToken);

        s3.HeadCalls.Should().ContainSingle().Which.BucketName.Should().Be(Bucket);
        s3.BucketCreations.Should().BeEmpty();
        s3.VersioningCalls.Should().ContainSingle()
            .Which.VersioningConfig!.Status.Should().Be(VersionStatus.Enabled);
        log.OfLevel(LogLevel.Warning).Should().BeEmpty("在るバケットに対して警告を出してはならない（#1562）");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NotFound")] // HEAD は本文が無く、SDK は状態コード由来のコードを載せる
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")] // 本文を返す実装
    [InlineData(HttpStatusCode.NotFound, "")] // コードが空でも 404 なら無い
    public async Task 無いとき_404なら作成し版管理を有効化する(HttpStatusCode status, string code)
    {
        var s3 = new FakeS3(() => S3Error(status, code));
        var (sut, log) = Sut(s3);

        await sut.EnsureBucketAsync(TestContext.Current.CancellationToken);

        s3.BucketCreations.Should().ContainSingle().Which.BucketName.Should().Be(Bucket);
        s3.VersioningCalls.Should().ContainSingle();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")] // SeaweedFS の GetBucketAcl が返していた形
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")] // 在るかもしれない（他者のバケット・権限不足）
    [InlineData(HttpStatusCode.InternalServerError, "InternalError")]
    public async Task 不明なとき_作成も版の設定もせず警告して戻る(HttpStatusCode status, string code)
    {
        // 🔴 原則 A: 不明は無いではない。作成を撃つと「在るバケットへの失敗」を「作れば直る」と取り違える。
        var s3 = new FakeS3(() => S3Error(status, code));
        var (sut, log) = Sut(s3);

        await sut.EnsureBucketAsync(TestContext.Current.CancellationToken);

        s3.BucketCreations.Should().BeEmpty("不明を無いとして扱ってはならない");
        s3.VersioningCalls.Should().BeEmpty();
        var warning = log.OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain("existence is unknown").And.Contain(((int)status).ToString());
        warning.Message.Should().Contain("first write will create the bucket and retry",
            "無かった場合の回収先（書き込み時の自己修復）を運用者に示す");
    }

    [Fact]
    public async Task 接続できないときも不明として扱い投げない()
    {
        // 起動順の競合（ストアがまだ待ち受けていない）も不明である。起動は止めない（fail-open は従来どおり）。
        var s3 = new FakeS3(() => new HttpRequestException("connection refused"));
        var (sut, log) = Sut(s3);

        var act = async () => await sut.EnsureBucketAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        s3.BucketCreations.Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(nameof(HttpRequestException));
    }

    // ［#1630］取り消しは **AWS SDK（HttpClient）が表す形** —— 呼び出し側の token を持つ `TaskCanceledException` —— で、
    // HeadBucket の最中に起こす。素の `OperationCanceledException` を注入していた間は、絞り込みを「`TaskCanceledException` なら
    // 時間切れ（不明）」と**型で**判定する変異（`|| ex is TaskCanceledException`）が生き残った。
    [Fact]
    public async Task 取り消しは握らずに投げる()
    {
        using var cts = new CancellationTokenSource();
        var injected = new TaskCanceledException("注入した呼び出し側の取り消し", null, cts.Token);
        var s3 = new FakeS3(() =>
        {
            cts.Cancel();
            return injected;
        });
        var (sut, log) = Sut(s3);

        var act = async () => await sut.EnsureBucketAsync(cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().BeSameAs(injected, "取り消しを「不明」へ畳まず、そのまま伝えている");
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task 版管理が無効なら在っても版の設定をしない()
    {
        var s3 = new FakeS3(() => null);
        var (sut, _) = Sut(s3, versioning: false);

        await sut.EnsureBucketAsync(TestContext.Current.CancellationToken);

        s3.BucketCreations.Should().BeEmpty();
        s3.VersioningCalls.Should().BeEmpty();
    }
}
