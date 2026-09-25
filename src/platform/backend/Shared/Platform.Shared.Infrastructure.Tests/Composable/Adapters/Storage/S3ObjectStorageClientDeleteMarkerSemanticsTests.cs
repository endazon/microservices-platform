using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-06, FR-19, ADR-0057 決定 1, ADR-0106 決定 4, IADR-0296（2026-09-26 追記）, [[IADR-0461]] (#1499):
// `DeleteAsync` が **S3 の版管理の意味論どおりに振る舞うストア**でも、版を 1 つも残さないことを固定する。
//
// 🔴 **なぜ別クラスか。** 隣の `S3ObjectStorageClientDeleteTests` の偽物は「決まった一覧を返し、削除要求を
// 記録するだけ」であり、**削除がストアの状態をどう変えるか**を持たない。そのため、全版を消した後に
// versionId 無しの削除を 1 回撃つ旧実装は、そこでは緑のままだった ——
// **バージョニングが有効なバケットでの versionId 無しの削除は、対象が無くても delete marker を作る**
// （AWS S3 の仕様であり、SeaweedFS 4.47 も `weed/s3api/s3api_object_handlers_delete.go` で同じく作る）。
// SeaweedFS の実イメージで受け入れ試験 `ObjectStorageRoundTripTests.Delete_removes_every_version` が
// 落ちて初めて見つかった（Integration run 36147130563）。MinIO では marker が残らなかった。
//
// 本クラスの偽物はバケットの状態を持ち、その意味論を写す。Docker なしで PR の ci.yml が走らせる。
public class S3ObjectStorageClientDeleteMarkerSemanticsTests
{
    private const string Bucket = "test-bucket";
    private const string Key = "documents/abc/body.md";
    private static string Uri => StorageUri.Build(Bucket, Key);

    private enum Versioning { Off, Enabled, Suspended }

    // S3 の版管理の意味論を写した偽の S3（1 バケット）。base を呼ばないのでネットワーク I/O は無い。
    //
    //   - 版は新しい順（先頭が最新）に持つ。delete marker も 1 つの版である。
    //   - versionId 付きの削除: その版だけを消す（marker でも同じ）。
    //   - versionId 無しの削除:
    //       Enabled   … **対象の有無にかかわらず** 新しい delete marker を積む（AWS / SeaweedFS の挙動）
    //       Suspended … "null" 版を null の delete marker で置き換える
    //       Off       … "null" 版（唯一の版）を消す
    //   - ListVersions: Prefix の前方一致で全キーの版を返す（Off のバケットでも versionId "null" として返る）。
    private sealed class VersionedBucketS3(Versioning versioning)
        : AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"),
            new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true })
    {
        private readonly Dictionary<string, List<S3ObjectVersion>> _versions = [];
        private int _next;

        public void Seed(string key, int count)
        {
            for (var i = 0; i < count; i++) Put(key);
        }

        public IReadOnlyList<S3ObjectVersion> VersionsOf(string key) =>
            _versions.TryGetValue(key, out var list) ? list : [];

        private void Put(string key)
        {
            var list = Versions(key);
            var id = versioning == Versioning.Enabled ? $"v{++_next}" : "null";
            if (id == "null") list.RemoveAll(v => v.VersionId == "null");
            list.Insert(0, new S3ObjectVersion { Key = key, VersionId = id, IsDeleteMarker = false });
        }

        private List<S3ObjectVersion> Versions(string key)
        {
            if (!_versions.TryGetValue(key, out var list)) _versions[key] = list = [];
            return list;
        }

        public override Task<ListVersionsResponse> ListVersionsAsync(
            ListVersionsRequest request, CancellationToken cancellationToken = default)
        {
            var hits = _versions
                .Where(kv => kv.Key.StartsWith(request.Prefix ?? "", StringComparison.Ordinal))
                .SelectMany(kv => kv.Value.Select((v, i) => new S3ObjectVersion
                {
                    Key = v.Key, VersionId = v.VersionId, IsDeleteMarker = v.IsDeleteMarker, IsLatest = i == 0
                }))
                .ToList();
            return Task.FromResult(new ListVersionsResponse { Versions = hits, IsTruncated = false });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(
            DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            var list = Versions(request.Key);
            if (!string.IsNullOrEmpty(request.VersionId))
            {
                list.RemoveAll(v => v.VersionId == request.VersionId);
                return Task.FromResult(new DeleteObjectResponse());
            }

            switch (versioning)
            {
                case Versioning.Enabled:
                    list.Insert(0, new S3ObjectVersion { Key = request.Key, VersionId = $"m{++_next}", IsDeleteMarker = true });
                    break;
                case Versioning.Suspended:
                    list.RemoveAll(v => v.VersionId == "null");
                    list.Insert(0, new S3ObjectVersion { Key = request.Key, VersionId = "null", IsDeleteMarker = true });
                    break;
                default:
                    list.RemoveAll(v => v.VersionId == "null");
                    break;
            }

            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    private static S3ObjectStorageClient Sut(IAmazonS3 s3) =>
        new(s3, new ObjectStorageOptions { Bucket = Bucket }, NullLogger<S3ObjectStorageClient>.Instance);

    // 受け入れ試験 Delete_removes_every_version と同じ形（3 回上書きしてから削除）を、Docker なしで。
    // 旧実装（全版削除の後に versionId 無しの削除）はここで delete marker を 1 つ残して落ちる。
    [Fact]
    public async Task 版管理が有効なバケットで全版を消した後にdeleteMarkerを残さない()
    {
        var s3 = new VersionedBucketS3(Versioning.Enabled);
        s3.Seed(Key, 3);

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.VersionsOf(Key).Should().BeEmpty(
            "delete marker を含め版が 1 つでも残れば ADR-0057 受け入れ基準①を満たさない");
    }

    // 対象が既に無い（二重削除・起動直後の再削除）ときも、marker を新しく作って残さない。削除は冪等である。
    [Fact]
    public async Task 版管理が有効なバケットで対象が無くてもdeleteMarkerを残さない()
    {
        var s3 = new VersionedBucketS3(Versioning.Enabled);

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);
        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.VersionsOf(Key).Should().BeEmpty("対象の無い削除が marker を積んで残してはならない");
    }

    // 版管理を停止したバケット: versionId 無しの削除は null の marker を作る。それも残さない。
    [Fact]
    public async Task 版管理を停止したバケットでも版を残さない()
    {
        var s3 = new VersionedBucketS3(Versioning.Suspended);
        s3.Seed(Key, 1);

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.VersionsOf(Key).Should().BeEmpty();
    }

    // IADR-0296 決定 1 の意図（版管理の無いバケットの取りこぼしを塞ぐ）を保つ。
    [Fact]
    public async Task 版管理の無いバケットでも実体を消す()
    {
        var s3 = new VersionedBucketS3(Versioning.Off);
        s3.Seed(Key, 1);

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.VersionsOf(Key).Should().BeEmpty();
    }

    // 前方一致の隣のキーは、版管理の意味論の下でも巻き込まない。
    [Fact]
    public async Task 前方一致の隣のキーの版は残す()
    {
        var s3 = new VersionedBucketS3(Versioning.Enabled);
        s3.Seed(Key, 2);
        s3.Seed(Key + ".bak", 2);

        await Sut(s3).DeleteAsync(Uri, TestContext.Current.CancellationToken);

        s3.VersionsOf(Key).Should().BeEmpty();
        s3.VersionsOf(Key + ".bak").Should().HaveCount(2);
    }
}
