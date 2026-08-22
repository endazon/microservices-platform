using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// NFR / #901: AddPlatformObjectStorage の IsConfigured 分岐を固定する。本番 5 箇所。
//
// 🔴 **この分岐が退行すると本番の書き込みが黙って捨てられる。**
// NullObjectStorageClient は縮退クライアントであり、書き込みを受け取っても何もしない。
// 資格情報が揃っているのに縮退側へ落ちれば、**例外も出ずログも正常のまま、保存だけが起きない**。
// 逆に未設定なのに実クライアントを構成すれば、起動はするが最初の書き込みで初めて落ちる。
//
// 分岐の**両側**を見る。片側だけだと「常にどちらか」に退行しても気付けない。
public class ObjectStorageExtensionsTests
{
    private static IConfiguration ConfigOf(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"ObjectStorage:{p.Key}", p => p.Value))
            .Build();

    private static IObjectStorageClient Resolve(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformObjectStorage(config);
        return services.BuildServiceProvider().GetRequiredService<IObjectStorageClient>();
    }

    [Fact]
    public void 資格情報が揃っていれば実クライアントを登録する()
    {
        var client = Resolve(ConfigOf(
            ("Endpoint", "http://minio:9000"), ("AccessKey", "ak"), ("SecretKey", "sk")));

        client.Should().BeOfType<S3ObjectStorageClient>(
            "縮退側へ落ちると、例外もログも出ないまま本番の書き込みが捨てられる");
    }

    [Fact]
    public void 設定が無ければ縮退クライアントを登録する()
    {
        var client = Resolve(ConfigOf());

        client.Should().BeOfType<NullObjectStorageClient>(
            "未設定で実クライアントを構成すると、起動は通り最初の書き込みで初めて落ちる");
    }

    // IsConfigured は 3 つの条件の AND である。**どれか 1 つでも欠ければ縮退する**ことを
    // 個別に見る（AND の項が 1 つ落ちても、他の 2 つで緑に見える形を潰す）。
    [Theory]
    [InlineData(null, "ak", "sk")]
    [InlineData("http://minio:9000", null, "sk")]
    [InlineData("http://minio:9000", "ak", null)]
    [InlineData("", "ak", "sk")]
    [InlineData("http://minio:9000", "  ", "sk")]
    public void 三条件のどれか1つでも欠ければ縮退する(string? endpoint, string? accessKey, string? secretKey)
    {
        var client = Resolve(ConfigOf(
            ("Endpoint", endpoint), ("AccessKey", accessKey), ("SecretKey", secretKey)));

        client.Should().BeOfType<NullObjectStorageClient>();
    }

    [Fact]
    public void 設定オブジェクト自体も登録され既定値を持つ()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformObjectStorage(ConfigOf(("Endpoint", "http://minio:9000")));

        var options = services.BuildServiceProvider().GetRequiredService<ObjectStorageOptions>();

        options.Endpoint.Should().Be("http://minio:9000");
        options.Bucket.Should().Be("knowledge-normalized");
        options.ForcePathStyle.Should().BeTrue("MinIO は仮想ホスト形式ではなくパス形式を使う");
    }
}
