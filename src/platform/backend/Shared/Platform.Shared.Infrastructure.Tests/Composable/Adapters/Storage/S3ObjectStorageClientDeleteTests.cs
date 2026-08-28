using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, FR-19, ADR-0057 決定 1, IADR-0296: `S3ObjectStorageClient.DeleteAsync` が
// **全バージョンを消す**ことを、実 I/O なしで固定する。
//
// 🔴 **「消えた」は素の DeleteObject では作れない。** バケットのバージョニングは既定で有効であり、
// versionId 無しの削除は delete marker を 1 つ積むだけで過去の全版が残る。ADR-0057 の
// 受け入れ基準は「残っていない」ことなので、**版を列挙して 1 つずつ消す**必要がある。
// 本クラスは、その 3 つの壊れ方（列挙しない／最新 1 版だけ／ページを辿らない）を個別に落とす。
//
// 器は `AmazonS3Client` の派生である —— 本テストプロジェクトはモックライブラリを持たない
// （既存 `ObjectStorageBootstrapHostedServiceTests` と同じ制約）が、`ListVersionsAsync` と
// `DeleteObjectAsync` はいずれも `public virtual` である（SDK 4.0.100.2 で実測）。
// base を一度も呼ばないため**ネットワーク I/O は発生しない**。
public class S3ObjectStorageClientDeleteTests
{
    private const string Bucket = "test-bucket";
    private const string Key = "documents/abc/body.md";
    private static string Uri => StorageUri.Build(Bucket, Key);

    // 版を返す偽の S3。呼ばれた削除要求（キーと versionId）をすべて記録する。
    private sealed class FakeS3(params ListVersionsResponse[] pages)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        private int _page;

        public List<ListVersionsRequest> ListCalls { get; } = [];
        public List<DeleteObjectRequest> DeleteCalls { get; } = [];

        public override Task<ListVersionsResponse> ListVersionsAsync(
            ListVersionsRequest request, CancellationToken cancellationToken = default)
        {
            ListCalls.Add(request);
            var page = _page < pages.Length ? pages[_page] : new ListVersionsResponse();
            _page++;
            return Task.FromResult(page);
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(
            DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(request);
            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    private static S3ObjectStorageClient Sut(IAmazonS3 s3) =>
        new(s3, new ObjectStorageOptions { Bucket = Bucket },
            NullLogger<S3ObjectStorageClient>.Instance);

    private static S3ObjectVersion Version(string id, bool latest = false, bool marker = false,
        string key = Key) =>
        new() { Key = key, VersionId = id, IsLatest = latest, IsDeleteMarker = marker };

    // 変異 2「delete marker だけ書く（版列挙をしない）」を落とす。
    // 版を列挙して versionId 付きで消していなければ、記録される削除要求は versionId が無いもの
    // だけになる（＝ delete marker を積んだだけ）。
    [Fact]
    public async Task 全バージョンをversionId付きで削除する()
    {
        var s3 = new FakeS3(new ListVersionsResponse
        {
            Versions = [Version("v3", latest: true), Version("v2"), Version("v1")]
        });

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.ListCalls.Should().ContainSingle("版を列挙せずに消すと過去版が残る")
            .Which.Prefix.Should().Be(Key);
        s3.DeleteCalls.Where(d => d.VersionId is not null).Select(d => d.VersionId)
            .Should().BeEquivalentTo(["v1", "v2", "v3"],
                "全バージョンを versionId 付きで消さないと delete marker を積むだけになる");
        s3.DeleteCalls.Should().OnlyContain(d => d.BucketName == Bucket && d.Key == Key);
    }

    // 変異 3「版列挙を最新 1 版に絞る」を落とす。IsLatest だけを消す実装は v3 しか消さない。
    [Fact]
    public async Task 最新版だけでなく過去版も消す()
    {
        var s3 = new FakeS3(new ListVersionsResponse
        {
            Versions = [Version("v3", latest: true), Version("v2"), Version("v1")]
        });

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.DeleteCalls.Select(d => d.VersionId).Should()
            .Contain(["v1", "v2"], "IsLatest だけを消す実装では過去版が残る");
    }

    // delete marker 自身も版として消す（残すとオブジェクトは「削除済みの版」として残り続ける）。
    // .NET SDK は marker を Versions へ混ぜて返す（ListVersionsResponse に DeleteMarkers は無い）。
    [Fact]
    public async Task deleteMarkerも版として消す()
    {
        var s3 = new FakeS3(new ListVersionsResponse
        {
            Versions = [Version("m1", latest: true, marker: true), Version("v1")]
        });

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.DeleteCalls.Select(d => d.VersionId).Should().Contain("m1",
            "delete marker を残すとオブジェクトは削除済みの版として残り続ける");
    }

    // ページングを辿らない実装（IsTruncated を見ない）を落とす。1 応答は既定 1000 件までしか返らない。
    [Fact]
    public async Task 版の一覧が切り詰められていれば続きを辿る()
    {
        var s3 = new FakeS3(
            new ListVersionsResponse
            {
                Versions = [Version("v3", latest: true), Version("v2")],
                IsTruncated = true,
                NextKeyMarker = Key,
                NextVersionIdMarker = "v2"
            },
            new ListVersionsResponse { Versions = [Version("v1")], IsTruncated = false });

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.ListCalls.Should().HaveCount(2, "IsTruncated を見ないと 2 ページ目の版が残る");
        s3.ListCalls[1].VersionIdMarker.Should().Be("v2");
        s3.DeleteCalls.Select(d => d.VersionId).Should().Contain("v1");
    }

    // Prefix は前方一致である。同じ接頭辞を持つ**別のキー**を巻き込んではならない。
    [Fact]
    public async Task 前方一致で紛れ込んだ別キーは消さない()
    {
        var s3 = new FakeS3(new ListVersionsResponse
        {
            Versions = [Version("v1", latest: true), Version("x1", key: Key + ".bak")]
        });

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.DeleteCalls.Select(d => d.VersionId).Should().NotContain("x1",
            "Prefix は前方一致なので Key の厳密一致で絞らないと隣のキーを消す");
    }

    // バージョニング無効のバケット（版が 1 つも返らない）でも削除は撃たれる。
    [Fact]
    public async Task 版が無くても素の削除を撃つ()
    {
        var s3 = new FakeS3(new ListVersionsResponse());

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.DeleteCalls.Should().ContainSingle()
            .Which.VersionId.Should().BeNull();
    }

    // storage:// でない URI は誤って別のものを消す前に弾く。
    [Fact]
    public async Task storage以外のURIは例外にする()
    {
        var s3 = new FakeS3();

        var act = async () => await Sut(s3).DeleteAsync("https://example.com/x.md",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        s3.DeleteCalls.Should().BeEmpty();
    }
}
