using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, ADR-0095 決定 3, IADR-0433 決定 3, IADR-0453 決定 9 (#1411): allowlist ローダの fail-closed。
public class SecretItemCatalogTests
{
    private const string Valid = """
        {
          "vaultMount": "secret",
          "items": [
            { "item": "alpha", "vaultPath": "msp/alpha", "properties": ["api-key"] },
            { "item": "beta", "vaultPath": "msp/beta", "properties": ["from", "password"], "notWritable": ["host"] }
          ],
          "deferred": [ { "reason": "r", "vaultPaths": ["msp/gamma"] } ],
          "excluded": [ { "reason": "r", "vaultPaths": ["msp/delta"] } ]
        }
        """;

    // SC-22, IADR-0433 決定 3: 実ファイル（単一情報源）を読める。4 KV・13 プロパティ（IADR-0433 §結果の数）。
    [Fact]
    public void Loads_the_repository_allowlist()
    {
        var catalog = SecretItemCatalog.Load(RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json"));

        catalog.VaultMount.Should().Be("secret");
        catalog.Items.Select(i => i.Item).Should().Equal("llm-provider-credentials", "keycloak-smtp", "wikijs-sync", "ast-app-secrets");
        catalog.Items.Sum(i => i.Properties.Count).Should().Be(13);
        // 🔴 notWritable（構成）は書けるプロパティに入らない。
        catalog.Find("keycloak-smtp")!.Properties.Should().Equal("from", "user", "password");
    }

    // SC-22, IADR-0433 決定 3: 出力ディレクトリへ同梱されている（BFF の既定の置き場）。
    [Fact]
    public void The_allowlist_is_shipped_next_to_the_assembly()
    {
        File.Exists(Path.Combine(AppContext.BaseDirectory, SecretItemCatalog.DefaultFileName)).Should().BeTrue();
    }

    // SC-22, IADR-0433 決定 3: items[] だけが allowlist。deferred[] / excluded[] は読まない（陽性対照つき）。
    [Fact]
    public void Only_items_are_allowlisted()
    {
        var catalog = SecretItemCatalog.Parse(Valid, "inline");

        catalog.Find("alpha").Should().NotBeNull();
        catalog.Items.Should().HaveCount(2);
        catalog.Items.Should().NotContain(i => i.VaultPath == "msp/gamma" || i.VaultPath == "msp/delta");
        catalog.Find("gamma").Should().BeNull();
    }

    // SC-22, IADR-0433 決定 3: 書けるプロパティと書けないプロパティが交差したら起動しない。
    [Fact]
    public void Overlap_between_properties_and_notWritable_fails_closed()
    {
        var json = Valid.Replace("\"notWritable\": [\"host\"]", "\"notWritable\": [\"password\"]", StringComparison.Ordinal);

        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>().WithMessage("*交差*");
    }

    // SC-22, IADR-0433 決定 1・3: ワイルドカード・`..` を含むパスは拒む（policy の完全一致と食い違わせない）。
    [Theory]
    [InlineData("msp/*")]
    [InlineData("msp/+")]
    [InlineData("msp/../postgres")]
    [InlineData("/msp/alpha")]
    public void Wildcard_or_traversal_paths_fail_closed(string vaultPath)
    {
        var json = Valid.Replace("\"msp/alpha\"", $"\"{vaultPath}\"", StringComparison.Ordinal);

        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>();
    }

    // SC-22, IADR-0433 決定 3: 空の items[]・壊れた JSON・items[] の欠落はいずれも起動しない（空の allowlist にしない）。
    [Theory]
    [InlineData("""{ "vaultMount": "secret", "items": [] }""")]
    [InlineData("""{ "vaultMount": "secret" }""")]
    [InlineData("""{ "vaultMount": "secret", "items": [ { "item": "a", "vaultPath": "msp/a", "properties": [] } ] }""")]
    [InlineData("{ not json")]
    public void Empty_or_broken_allowlists_fail_closed(string json)
    {
        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>();
    }

    // SC-22, IADR-0433 決定 3: ファイルを読めなければ起動しない（「読めなかったから全部許す」にしない）。
    [Fact]
    public void Unreadable_file_fails_closed()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"sc22-missing-{Guid.NewGuid():N}.json");

        var act = () => SecretItemCatalog.Load(missing);

        act.Should().Throw<SecretItemCatalogException>().WithMessage("*fail-closed*");
    }
}
