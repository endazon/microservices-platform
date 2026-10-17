using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, NFR-16, [[IADR-0544]] 決定 4（2026-10-17 追記 / #1940）: 上限つきの本文の取得（`GetTextBoundedAsync`）を実 I/O 無しで固定する。
//
// 🔴 **上限を超える本文は全文を読まない。** ContentLength が上限を超えれば本文ストリームから 1 バイトも読まず、
// ContentLength が無い・偽っている場合も読むのは上限＋1 バイトまでである。読んだ量は器のストリームが数え、
// 割り当ては `GC.GetAllocatedBytesForCurrentThread` で測る（器はすべて同期で完了するので、計測は同じスレッドに閉じる）。
public class S3ObjectStorageClientBoundedReadTests
{
    private const string Uri = "storage://knowledge-normalized/documents/abc/body.md";

    // 長さ `length` の 'a' を、読まれた分だけその場で生む（本文そのものをメモリに置かない）。読んだバイト数を数える。
    private sealed class CountingStream(long length) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            buffer.AsSpan(offset, n).Fill((byte)'a');
            _position += n;
            BytesRead += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, length - _position);
            if (n <= 0) return ValueTask.FromResult(0);
            buffer.Span[..n].Fill((byte)'a');
            _position += n;
            BytesRead += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeS3(Func<GetObjectResponse> respond)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        public List<GetObjectRequest> GetCalls { get; } = [];

        public override Task<GetObjectResponse> GetObjectAsync(
            GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            GetCalls.Add(request);
            return Task.FromResult(respond());
        }
    }

    private static GetObjectResponse Response(Stream body, long contentLength) => new()
    {
        HttpStatusCode = HttpStatusCode.OK,
        ResponseStream = body,
        ContentLength = contentLength,
    };

    private static S3ObjectStorageClient Sut(IAmazonS3 s3) =>
        new(s3, new ObjectStorageOptions { Bucket = "knowledge-normalized" }, NullLogger<S3ObjectStorageClient>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long Limit = 16L * 1024 * 1024;

    // AC-1: 大きさ（ContentLength）が上限を超えれば、本文を 1 バイトも読まずに超過を返す。割り当ては本文の大きさに比例しない。
    [Fact]
    public async Task ContentLengthが上限を超えれば本文を読まずに超過を返し_割り当ても本文に比例しない()
    {
        const long huge = 512L * 1024 * 1024; // 512 MiB（document-service の limits.memory と同じ大きさ）
        var body = new CountingStream(huge);
        var sut = Sut(new FakeS3(() => Response(body, huge)));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = await sut.GetTextBoundedAsync(Uri, Limit, Ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        read.Exceeded.Should().BeTrue();
        read.Text.Should().BeNull("切り詰めた本文は返さない");
        read.SizeBytes.Should().Be(huge);
        body.BytesRead.Should().Be(0, "上限を超える本文は 1 バイトも読まない");
        allocated.Should().BeLessThan(1024 * 1024, "本文の大きさ（512 MiB）も上限（16 MiB）も割り当てない");
    }

    // AC-1: ContentLength が無い（0）・偽っている場合も、読むのは上限＋1 バイトまでで打ち切る（大きさは下限＝上限＋1）。
    [Theory]
    [InlineData(0L)]
    [InlineData(1024L)]
    public async Task ContentLengthが無い_偽っていても上限プラス1バイトで打ち切る(long declared)
    {
        const long actual = 256L * 1024 * 1024;
        var body = new CountingStream(actual);
        var sut = Sut(new FakeS3(() => Response(body, declared)));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = await sut.GetTextBoundedAsync(Uri, Limit, Ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        read.Exceeded.Should().BeTrue();
        read.Text.Should().BeNull();
        read.SizeBytes.Should().Be(Limit + 1, "大きさが分からないときは下限を返す");
        body.BytesRead.Should().Be(Limit + 1, "上限＋1 バイト目で超過と分かれば、それ以上読まない");
        allocated.Should().BeLessThan(3 * Limit, "割り当ては上限に比例し、本文（256 MiB）に比例しない");
    }

    // AC-2: ちょうど上限は本文を返す（境界）。大きさは格納されたバイト数。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ちょうど上限は本文を返す(bool declaresLength)
    {
        var body = new CountingStream(Limit);
        var sut = Sut(new FakeS3(() => Response(body, declaresLength ? Limit : 0)));

        var read = await sut.GetTextBoundedAsync(Uri, Limit, Ct);

        read.Exceeded.Should().BeFalse();
        read.SizeBytes.Should().Be(Limit);
        read.Text!.Length.Should().Be((int)Limit);
        body.BytesRead.Should().Be(Limit);
    }

    // AC-2: 小さな本文は従来の `GetTextAsync`（StreamReader・UTF-8）と同じ文字列になる。BOM は落とす。
    [Fact]
    public async Task 上限以内の本文はUTF8で復号しBOMを落とす()
    {
        var text = "# 見出し\n本文 ✓";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var s3 = new FakeS3(() => Response(new MemoryStream(bytes), bytes.Length));

        var read = await Sut(s3).GetTextBoundedAsync(Uri, Limit, Ct);

        read.Should().Be(BoundedTextRead.Within(text, bytes.Length));
        s3.GetCalls.Should().ContainSingle();
        s3.GetCalls[0].BucketName.Should().Be("knowledge-normalized");
        s3.GetCalls[0].Key.Should().Be("documents/abc/body.md");
    }

    [Fact]
    public async Task 空の本文は空文字を返す()
    {
        var read = await Sut(new FakeS3(() => Response(new MemoryStream(), 0))).GetTextBoundedAsync(Uri, Limit, Ct);

        read.Should().Be(BoundedTextRead.Within(string.Empty, 0));
    }

    // NoSuchKey などの失敗は握らない（呼び出し側が `ObjectStorageErrors.IsNoSuchKey` で判定する）。
    [Fact]
    public async Task 取得の失敗は例外のまま上げる()
    {
        var sut = Sut(new FakeS3(() => throw new AmazonS3Exception("simulated")
        {
            StatusCode = HttpStatusCode.NotFound,
            ErrorCode = ObjectStorageErrors.NoSuchKeyErrorCode,
        }));

        var act = () => sut.GetTextBoundedAsync(Uri, Limit, Ct);

        await act.Should().ThrowAsync<AmazonS3Exception>();
    }
}
