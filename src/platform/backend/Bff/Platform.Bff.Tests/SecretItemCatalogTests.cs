using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, ADR-0095 決定 3, IADR-0433 決定 3, IADR-0453 決定 9 (#1411), IADR-0456 決定 1・4 (#1477): allowlist ローダの fail-closed。
public class SecretItemCatalogTests
{
    private const string Valid = """
        {
          "vaultMount": "secret",
          "items": [
            {
              "item": "alpha", "vaultPath": "msp/alpha", "properties": ["api-key"],
              "externalSecret": { "name": "alpha", "namespace": "microservices-platform" }
            },
            {
              "item": "beta", "vaultPath": "msp/beta", "properties": ["from", "password"], "notWritable": ["host"],
              "externalSecret": { "name": "beta", "namespace": "platform-infra" }
            }
          ],
          "deferred": [ { "reason": "r", "vaultPaths": ["msp/gamma"] } ],
          "excluded": [ { "reason": "r", "vaultPaths": ["msp/delta"] } ]
        }
        """;

    // SC-22, IADR-0433 決定 3, IADR-0456 決定 1 (#1477): 実ファイル（単一情報源）を読める。6 KV・20 プロパティ。
    [Fact]
    public void Loads_the_repository_allowlist()
    {
        var catalog = SecretItemCatalog.Load(RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json"));

        catalog.VaultMount.Should().Be("secret");
        catalog.Items.Select(i => i.Item).Should().Equal(
            "llm-provider-credentials", "keycloak-smtp", "wikijs-sync", "ast-app-secrets", "ast-moomoo", "ast-moomoo-rsa");
        catalog.Items.Sum(i => i.Properties.Count).Should().Be(20);
        // 🔴 notWritable（構成）は書けるプロパティに入らない。
        catalog.Find("keycloak-smtp")!.Properties.Should().Equal("from", "user", "password");
    }

    // SC-22, IADR-0456 決定 1〜3 (#1477): 契約の表（#1477）どおりの種別と sensitive を持つ。
    [Fact]
    public void Repository_allowlist_declares_the_contract_kinds()
    {
        var catalog = SecretItemCatalog.Load(RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json"));

        var moomoo = catalog.Find("ast-moomoo")!;
        moomoo.VaultPath.Should().Be("ai-stock-trading/moomoo");
        moomoo.PropertyDefinitions.Should().Equal(
            new SecretPropertyDefinition("login-account", SecretPropertyKind.Value, true),
            new SecretPropertyDefinition("login-pwd-md5", SecretPropertyKind.Md5FromPassword, true));

        var rsa = catalog.Find("ast-moomoo-rsa")!;
        rsa.VaultPath.Should().Be("ai-stock-trading/moomoo-rsa");
        rsa.PropertyDefinitions.Should().Equal(new SecretPropertyDefinition("opend_rsa.pem", SecretPropertyKind.GenerateRsaPkcs1, true));

        var app = catalog.Find("ast-app-secrets")!;
        app.PropertyDefinitions.Where(p => !p.Sensitive).Select(p => p.Name).Should().Equal(
            "discord-bot-guild-id", "discord-bot-channel-id", "discord-bot-allowed-user-ids", "discord-bot-user-mapping");
        app.PropertyDefinitions.Should().OnlyContain(p => p.Kind == SecretPropertyKind.Value);
        // 🔴 realm と対の *-auth-client-* は書けない（陽性対照: 外部 API キーは書ける）。
        app.Properties.Should().Contain("finnhub-api-key").And.NotContain(p => p.Contains("auth-client", StringComparison.Ordinal));
    }

    // SC-22, IADR-0456 決定 4 (#1477): 各項目の同期先 ExternalSecret（契約の表の Secret 名と一致させる）。
    [Fact]
    public void Repository_allowlist_maps_each_item_to_its_external_secret()
    {
        var catalog = SecretItemCatalog.Load(RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json"));

        catalog.Items.ToDictionary(i => i.Item, i => i.ExternalSecret).Should().Equal(new Dictionary<string, ExternalSecretReference>
        {
            ["llm-provider-credentials"] = new("llm-provider-credentials", "microservices-platform"),
            ["keycloak-smtp"] = new("keycloak-smtp", "platform-infra"),
            ["wikijs-sync"] = new("wikijs-sync", "microservices-platform"),
            ["ast-app-secrets"] = new("ast-secrets", "ai-stock-trading"),
            ["ast-moomoo"] = new("moomoo-credentials", "ai-stock-trading"),
            ["ast-moomoo-rsa"] = new("moomoo-rsa", "ai-stock-trading"),
        });
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

    // SC-22, IADR-0456 決定 1: 文字列の要素は kind=value・sensitive=true。オブジェクトの要素は宣言を読む。
    [Fact]
    public void Property_objects_carry_kind_and_sensitive()
    {
        var json = Valid.Replace("\"properties\": [\"api-key\"]",
            "\"properties\": " + """["api-key", { "name": "pwd", "kind": "md5-from-password" }, { "name": "key.pem", "kind": "generate-rsa-pkcs1" }, { "name": "guild", "sensitive": false }]""",
            StringComparison.Ordinal);

        var alpha = SecretItemCatalog.Parse(json, "inline").Find("alpha")!;

        alpha.PropertyDefinitions.Should().Equal(
            new SecretPropertyDefinition("api-key", SecretPropertyKind.Value, true),
            new SecretPropertyDefinition("pwd", SecretPropertyKind.Md5FromPassword, true),
            new SecretPropertyDefinition("key.pem", SecretPropertyKind.GenerateRsaPkcs1, true),
            new SecretPropertyDefinition("guild", SecretPropertyKind.Value, false));
        alpha.FindProperty("pwd")!.KindName.Should().Be("md5-from-password");
        alpha.FindProperty("nope").Should().BeNull();
    }

    // SC-22, IADR-0433 決定 3: 書けるプロパティと書けないプロパティが交差したら起動しない。
    [Fact]
    public void Overlap_between_properties_and_notWritable_fails_closed()
    {
        var json = Valid.Replace("\"notWritable\": [\"host\"]", "\"notWritable\": [\"password\"]", StringComparison.Ordinal);

        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>().WithMessage("*交差*");
    }

    // SC-22, IADR-0456 決定 1: オブジェクトの要素の名前も notWritable との交差の対象になる。
    [Fact]
    public void Overlap_with_a_property_object_fails_closed()
    {
        var json = Valid.Replace("\"properties\": [\"from\", \"password\"], \"notWritable\": [\"host\"]",
            "\"properties\": " + """["from", { "name": "host", "sensitive": false }], "notWritable": ["host"]""", StringComparison.Ordinal);

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

    // SC-22, IADR-0456 決定 1: 種別・sensitive・要素の形の不正は起動しない。
    // 🔴 `sensitive: false` を value 以外へ付ける宣言（パスワード・生成鍵を「秘密でない」とする）も拒む。
    [Theory]
    [InlineData("""{ "name": "pwd", "kind": "sha1-from-password" }""", "未知")]
    [InlineData("""{ "name": "pwd", "knid": "md5-from-password" }""", "未知のキー")]
    [InlineData("""{ "name": "pwd", "kind": 1 }""", "文字列ではない")]
    [InlineData("""{ "name": "pwd", "sensitive": "false" }""", "真偽値ではない")]
    [InlineData("""{ "name": "pwd", "kind": "md5-from-password", "sensitive": false }""", "sensitive: false")]
    [InlineData("""{ "name": "key.pem", "kind": "generate-rsa-pkcs1", "sensitive": false }""", "sensitive: false")]
    [InlineData("""{ "kind": "md5-from-password" }""", "書式が不正")]
    [InlineData("""{ "name": "api-key" }""", "重複")]
    [InlineData("42", "文字列でもオブジェクトでもない")]
    public void Invalid_property_declarations_fail_closed(string entry, string reason)
    {
        var json = Valid.Replace("\"properties\": [\"api-key\"]", $"\"properties\": [\"api-key\", {entry}]", StringComparison.Ordinal);

        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>().WithMessage($"*{reason}*");
    }

    // SC-22, IADR-0456 決定 4: ExternalSecret の宣言が無い・書式違反・重複は起動しない（同期先の無い項目を黙って混ぜない）。
    [Theory]
    [InlineData("""{ "name": "alpha", "namespace": "microservices-platform" }""", null, "externalSecret が無い")]
    [InlineData("""{ "name": "alpha", "namespace": "microservices-platform" }""", """{ "name": "Alpha_1", "namespace": "microservices-platform" }""", "name の書式")]
    [InlineData("""{ "name": "alpha", "namespace": "microservices-platform" }""", """{ "name": "alpha", "namespace": "ns/other" }""", "namespace の書式")]
    [InlineData("""{ "name": "alpha", "namespace": "microservices-platform" }""", """{ "name": "alpha" }""", "namespace が無い")]
    [InlineData("""{ "name": "beta", "namespace": "platform-infra" }""", """{ "name": "alpha", "namespace": "microservices-platform" }""", "重複")]
    public void Invalid_external_secret_declarations_fail_closed(string original, string? replacement, string reason)
    {
        var json = replacement is null
            ? Valid.Replace($",\n      \"externalSecret\": {original}", "", StringComparison.Ordinal)
                .Replace($",\r\n      \"externalSecret\": {original}", "", StringComparison.Ordinal)
            : Valid.Replace(original, replacement, StringComparison.Ordinal);
        json.Should().NotBe(Valid, "置換が効いていること（試験の前提）");

        var act = () => SecretItemCatalog.Parse(json, "inline");

        act.Should().Throw<SecretItemCatalogException>().WithMessage($"*{reason}*");
    }

    // SC-22, IADR-0433 決定 3: 空の items[]・壊れた JSON・items[] の欠落はいずれも起動しない（空の allowlist にしない）。
    [Theory]
    [InlineData("""{ "vaultMount": "secret", "items": [] }""")]
    [InlineData("""{ "vaultMount": "secret" }""")]
    [InlineData("""{ "vaultMount": "secret", "items": [ { "item": "a", "vaultPath": "msp/a", "properties": [], "externalSecret": { "name": "a", "namespace": "n" } } ] }""")]
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
