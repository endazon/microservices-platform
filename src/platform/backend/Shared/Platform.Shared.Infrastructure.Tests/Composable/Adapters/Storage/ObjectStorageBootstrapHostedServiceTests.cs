using Amazon.Runtime;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// NFR / #939（親: #901）: ObjectStorageBootstrapHostedService はプロセス内で観測できる範囲を固定する。
//
// 🔴 このホストサービスは「壊れても起動は成功し続ける」設計である
// （スキップ経路も例外握り潰し経路も、起動そのものは通す）。
// スキップ条件は `client is not S3ObjectStorageClient s3 || !options.EnsureBucketOnStartup` という
// OR 結合であり、**両側それぞれを単独で** スキップさせられるケースを用意しないと、
// 片方の退行が緑のまま残る（issue 本文の変異試験対象そのもの）。
//
// 🔴 `EnsureBucketAsync` の成功経路・例外握り潰し経路は MinIO が要るためスコープ外
// （`ObjectStorageRoundTripTests` が `[Trait("Category","Integration")]` + `[DockerFact]` で持つ）。
// 単体側は「その 2 経路を検出できないこと」を issue 本文どおり明記するに留める。
public class ObjectStorageBootstrapHostedServiceTests
{
    // スキップ経路は await を 1 つも通らないため、呼び出した瞬間に返る Task は
    // 既に RanToCompletion である（async Task メソッドの一般規則）。
    // EnsureBucketAsync まで到達すれば IAmazonS3 への await が挟まり、その瞬間には完了し得ない。
    // モックライブラリを持たない本テストプロジェクト（既存 ObjectStorageExtensionsTests.cs と同様）
    // で「到達しなかったこと」を環境非依存・決定的に見分けられる、唯一の観測点である。
    private static ObjectStorageBootstrapHostedService Sut(
        IObjectStorageClient client, ObjectStorageOptions options) =>
        new(client, options, NullLogger<ObjectStorageBootstrapHostedService>.Instance);

    // 実 S3 呼び出しへは到達しない設計（ケース B は EnsureBucketOnStartup=false でスキップする）
    // ため、接続先の実在は問わない。ネットワーク I/O は発生しない。
    private static S3ObjectStorageClient DummyS3Client(ObjectStorageOptions options)
    {
        var s3Config = new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:1",
            ForcePathStyle = true,
            AuthenticationRegion = options.Region
        };
        var s3 = new AmazonS3Client(new BasicAWSCredentials("dummy", "dummy"), s3Config);
        return new S3ObjectStorageClient(s3, options, NullLogger<S3ObjectStorageClient>.Instance);
    }

    // NFR / #939: 実クライアント未構成（NullObjectStorageClient）のとき StartAsync は何もせず正常終了する。
    [Fact]
    public void 実クライアント未構成のとき何もせず正常終了する()
    {
        var options = new ObjectStorageOptions { EnsureBucketOnStartup = true };
        var client = new NullObjectStorageClient(options, NullLogger<NullObjectStorageClient>.Instance);
        var sut = Sut(client, options);

        var task = sut.StartAsync(CancellationToken.None);

        task.IsCompletedSuccessfully.Should().BeTrue(
            "実クライアント未構成なら EnsureBucketAsync へは到達せず、await を経ずに即完了するはずである");
    }

    // NFR / #939: EnsureBucketOnStartup=false のときスキップする（クライアント型に関わらず）。
    [Fact]
    public void EnsureBucketOnStartupがfalseのときスキップする()
    {
        var options = new ObjectStorageOptions { EnsureBucketOnStartup = false };
        var client = DummyS3Client(options);
        var sut = Sut(client, options);

        var task = sut.StartAsync(CancellationToken.None);

        task.IsCompletedSuccessfully.Should().BeTrue(
            "EnsureBucketOnStartup=false ならクライアントが S3ObjectStorageClient でも " +
            "EnsureBucketAsync へは到達せず、即完了するはずである");
    }

    // NFR / #939: StopAsync は完了済みタスクを返す。
    [Fact]
    public void StopAsyncは完了済みタスクを返す()
    {
        var options = new ObjectStorageOptions();
        var client = new NullObjectStorageClient(options, NullLogger<NullObjectStorageClient>.Instance);
        var sut = Sut(client, options);

        var task = sut.StopAsync(CancellationToken.None);

        task.IsCompletedSuccessfully.Should().BeTrue();
    }
}
