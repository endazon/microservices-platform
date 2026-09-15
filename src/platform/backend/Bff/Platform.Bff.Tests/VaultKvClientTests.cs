using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, IADR-0453 フォローアップ 5, IADR-0454 決定 1 (#1467): `VaultKvClient` を端点を介さずに FakeVault へ直接つなぐ。
// 端点の試験（`BffSecretItemEndpointTests`）は HTTP の形を固定し、ここは**クライアントが返す結果の種類**を固定する。
//
// 🔴 **削除（`deletion_time`）と破棄（`destroyed`）の両方**を見る。片方しか見ない実装は、もう片方で `POST` を送り 403 を踏む。
// 陽性対照: KV が無いときは従来どおり `POST` ＋ `cas=0` で作る。
public class VaultKvClientTests
{
    private const string Mount = "secret";
    private const string Path = "msp/wikijs-sync";
    private const string Property = "apiKey";
    // 🔴 テスト用の明白なダミー値。本物の秘密は書かない。
    private const string PlaceholderValue = "placeholder-value-for-vault-client-tests";

    private readonly FakeVault _vault = new();

    private VaultKvClient CreateClient() => new(
        new SingleClientFactory(_vault.CreateHandler()),
        Options.Create(new VaultOptions { Address = FakeVault.Address }),
        new FakeVault.TokenReader(),
        TimeProvider.System,
        NullLogger<VaultKvClient>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_to_a_kv_whose_current_version_is_deleted_or_destroyed_reports_it_without_posting(bool destroyed)
    {
        var kv = _vault.Put(Path, (Property, "placeholder-existing"));
        if (destroyed) kv.Destroyed = true;
        else kv.Deleted = true;
        var client = CreateClient();

        var metadata = await client.ReadMetadataAsync(Mount, Path, TestContext.Current.CancellationToken);
        var written = await client.WritePropertyAsync(Mount, Path, Property, PlaceholderValue, TestContext.Current.CancellationToken);

        metadata.State.Should().Be(VaultMetadataState.Deleted);
        metadata.CurrentVersion.Should().BeNull();
        written.Outcome.Should().Be(VaultWriteOutcome.CurrentVersionDeleted);
        _vault.Requests.Should().NotContain(r => r.Method == "POST" && r.Path.StartsWith("/v1/secret/data/", StringComparison.Ordinal));
        kv.Version.Should().Be(1);
    }

    [Fact]
    public async Task Write_to_an_absent_kv_still_creates_it_with_cas_zero()
    {
        var client = CreateClient();

        var metadata = await client.ReadMetadataAsync(Mount, Path, TestContext.Current.CancellationToken);
        var written = await client.WritePropertyAsync(Mount, Path, Property, PlaceholderValue, TestContext.Current.CancellationToken);

        metadata.State.Should().Be(VaultMetadataState.Absent);
        written.Outcome.Should().Be(VaultWriteOutcome.Written);
        written.Version.Should().Be(1);
        _vault.Requests.Where(r => r.Path == "/v1/secret/data/" + Path).Select(r => r.Method)
            .Should().Equal("PATCH", "POST");
    }

    [Fact]
    public async Task Write_to_a_present_kv_patches_without_reading_metadata()
    {
        _vault.Put(Path, (Property, "placeholder-existing"));
        var client = CreateClient();

        var written = await client.WritePropertyAsync(Mount, Path, Property, PlaceholderValue, TestContext.Current.CancellationToken);

        written.Outcome.Should().Be(VaultWriteOutcome.Written);
        written.Version.Should().Be(2);
        _vault.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/secret/metadata/", StringComparison.Ordinal));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri(FakeVault.Address + "/") };
    }
}
