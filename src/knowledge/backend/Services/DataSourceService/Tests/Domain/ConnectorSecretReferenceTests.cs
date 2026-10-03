using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.ExternalServices;
using DataSourceService.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Domain;

// NFR-18, [[IADR-0493]] 決定 2・3 (#458 段 S0): 参照の形 `vault:<path>#<key>`、移送期間用の解決器、
// 資格情報の器が値を外へ出さないこと、コネクタが宣言する資格情報キーが応答のマスクと同じ集合に入ること。
[Trait("TestKind", "Unit")]
public sealed class ConnectorSecretReferenceTests
{
    [Theory]
    [InlineData("vault:datasource/ds-1#apiToken", "datasource/ds-1", "apiToken")]
    [InlineData("  vault:a/b/c#password  ", "a/b/c", "password")]
    [InlineData("VAULT:a#k", "a", "k")]
    public void TryParse_AcceptsTheReferenceForm(string value, string path, string key)
    {
        ConnectorSecretReference.TryParse(value, out var reference).Should().BeTrue();
        reference!.Path.Should().Be(path);
        reference.Key.Should().Be(key);
    }

    [Theory]
    [InlineData("vault:")]
    [InlineData("vault:a")]
    [InlineData("vault:#k")]
    [InlineData("vault:a#")]
    [InlineData("vault:a#b#c")]
    [InlineData("vault:a b#k")]
    [InlineData("plain-text")]
    public void TryParse_RejectsAnythingElse(string value)
        => ConnectorSecretReference.TryParse(value, out _).Should().BeFalse();

    // 🔴 接頭辞は大文字小文字を区別しない —— 区別すると `VAULT:…` が平文として外部へ送られる。
    [Theory]
    [InlineData("vault:x#y", true)]
    [InlineData("Vault:x#y", true)]
    [InlineData(" vault:x", true)]
    [InlineData("vaulty", false)]
    [InlineData("not vault:x#y", false)]
    public void LooksLikeReference_IsCaseInsensitive(string value, bool expected)
        => ConnectorSecretReference.LooksLikeReference(value).Should().Be(expected);

    [Fact]
    public void Reference_ToString_HidesThePath()
    {
        ConnectorSecretReference.TryParse("vault:datasource/hidden-path-segment#apiToken", out var reference);

        reference!.ToString().Should().NotContain("hidden-path-segment").And.Contain("apiToken");
    }

    // 移送期間用の解決器: 平文は素通し、参照は「解決器なし」、形の誤りは区別する。
    [Theory]
    [InlineData("plain-config-value", null)]
    [InlineData("vault:datasource/ds-1#apiToken", ConnectorSecretFailure.ResolverUnavailable)]
    [InlineData("VAULT:datasource/ds-1#apiToken", ConnectorSecretFailure.ResolverUnavailable)]
    [InlineData("vault:datasource/ds-1", ConnectorSecretFailure.MalformedReference)]
    public async Task Passthrough_PassesPlaintext_AndFailsClosedOnReferences(string value, ConnectorSecretFailure? failure)
    {
        var resolution = await new PlaintextPassthroughConnectorSecretResolver()
            .ResolveAsync(value, TestContext.Current.CancellationToken);

        resolution.Failure.Should().Be(failure);
        if (failure is null)
            resolution.Value.Should().Be(value);
        else
            resolution.Value.Should().BeNull("参照の文字列を値として返さない");
    }

    [Fact]
    public void ResolutionAndCredentials_ToString_DoNotCarryTheValue()
    {
        ConnectorSecretResolution.Resolved("resolved-sample-value").ToString().Should().NotContain("resolved-sample-value");
        new ConnectorCredentials(new Dictionary<string, string> { ["apiToken"] = "resolved-sample-value" })
            .ToString().Should().NotContain("resolved-sample-value").And.Contain("apiToken");
    }

    // コネクタが宣言する資格情報キーは、応答のマスク（`SecretMask.IsSecretKey`）が伏せる集合に入っていること。
    // 入っていなければ、参照（移送後）や平文（移送前）が GET 応答へ素で出る。
    [Fact]
    public void ConnectorCredentialKeys_AreAllMaskedInResponses()
    {
        IDataSourceConnector[] connectors =
        [
            new WikiConnector(null!, NullLogger<WikiConnector>.Instance),
            new SaaSConnector(null!, NullLogger<SaaSConnector>.Instance),
            new DatabaseConnector(null!, NullLogger<DatabaseConnector>.Instance),
            new FileSystemConnector(NullLogger<FileSystemConnector>.Instance),
        ];

        var keys = connectors.SelectMany(c => c.CredentialKeys).ToList();

        keys.Should().BeEquivalentTo(["apiToken", "apiToken", "password"], "資格情報を使う 3 コネクタが宣言する（前提）");
        keys.Should().AllSatisfy(k => SecretMask.IsSecretKey(k).Should().BeTrue());
    }
}
