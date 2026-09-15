using System.Text.RegularExpressions;
using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0433 決定 1・4・5, IADR-0453 決定 9 (#1411):
// **Vault policy の字面が allowlist（`items[]`）と完全一致すること**を固定する。
//
// 🔴 統制は「BFF が正しく実装されていれば」ではなく「policy の字面で読めること」にある（IADR-0433 §理由）。
// したがって字面そのものを測る —— path の集合・capability の集合・ワイルドカードの不在・
// role の束縛先（`bff`。`default` ではない）・ESO の role へ相乗りしていないこと。
public class SecretItemVaultPolicyTests
{
    private static readonly string PolicyPath = RepoPaths.Resolve("deploy/local/vault/eso/policy-bff-secret-write.hcl");
    private static readonly string CatalogPath = RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json");
    private static readonly string BootstrapPath = RepoPaths.Resolve("deploy/local/vault/eso/bootstrap.sh");

    private static readonly Regex PathBlock = new(
        """path\s+"(?<path>[^"]+)"\s*\{\s*capabilities\s*=\s*\[(?<caps>[^\]]*)\]\s*\}""",
        RegexOptions.Compiled);

    private static string PolicyWithoutComments() =>
        string.Join('\n', File.ReadAllLines(PolicyPath).Where(l => !l.TrimStart().StartsWith('#')));

    private static Dictionary<string, string[]> ParsePolicy()
    {
        var text = PolicyWithoutComments();
        var blocks = PathBlock.Matches(text);
        // 読めなかった path ブロックを黙って落とさない（解析器の取りこぼしで緑にならないように数を合わせる）。
        Regex.Matches(text, @"\bpath\s+""").Count.Should().Be(blocks.Count, "すべての path ブロックを解析できること");
        return blocks.ToDictionary(
            m => m.Groups["path"].Value,
            m => m.Groups["caps"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim('"')).Order(StringComparer.Ordinal).ToArray());
    }

    // SC-22, IADR-0433 決定 1: path の集合は items[] の data ＋ metadata と完全一致する（多くも少なくもない）。
    [Fact]
    public void Policy_paths_equal_the_allowlist_items_exactly()
    {
        var catalog = SecretItemCatalog.Load(CatalogPath);
        var expected = catalog.Items
            .SelectMany(i => new[] { $"{catalog.VaultMount}/data/{i.VaultPath}", $"{catalog.VaultMount}/metadata/{i.VaultPath}" })
            .Order(StringComparer.Ordinal);

        ParsePolicy().Keys.Order(StringComparer.Ordinal).Should().Equal(expected);
    }

    // SC-22, IADR-0433 決定 1・2, IADR-0453 決定 10: data は create/patch だけ（read も全置換の update も無い）、
    // metadata は read だけ。
    [Fact]
    public void Policy_grants_write_without_read_on_data_and_read_only_on_metadata()
    {
        foreach (var (path, caps) in ParsePolicy())
        {
            if (path.Contains("/data/", StringComparison.Ordinal))
                caps.Should().Equal(["create", "patch"], $"{path} は値を読み返せず、KV を全置換できない形であること");
            else
                caps.Should().Equal(["read"], $"{path} は版と時刻だけを読めること");
        }
    }

    // SC-22, IADR-0433 決定 1: ワイルドカード・list・delete・destroy・sudo を含まない。
    [Fact]
    public void Policy_has_no_wildcards_or_broad_capabilities()
    {
        var text = PolicyWithoutComments();
        text.Should().NotContain("*").And.NotContain("+");
        foreach (var forbidden in new[] { "\"list\"", "\"delete\"", "\"destroy\"", "\"sudo\"", "\"deny\"" })
            text.Should().NotContain(forbidden);
    }

    // SC-22, IADR-0433 決定 3: deferred[] / excluded[] のパスは policy に現れない（陽性対照: items[] のパスは在る）。
    // IADR-0456 (#1477): moomoo / moomoo-rsa は deferred[] から items[] へ移った。**外側の集合はファイルから引く**
    // （手で列挙すると、分類を動かしたときにこの試験だけが古い分類を守り続ける）。
    [Fact]
    public void Policy_does_not_cover_deferred_or_excluded_paths()
    {
        var paths = ParsePolicy().Keys.ToList();
        paths.Should().Contain("secret/data/msp/llm-provider-credentials")
            .And.Contain("secret/data/ai-stock-trading/moomoo")
            .And.Contain("secret/data/ai-stock-trading/moomoo-rsa");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CatalogPath));
        var outside = new[] { "deferred", "excluded" }
            .SelectMany(section => document.RootElement.GetProperty(section).EnumerateArray())
            .SelectMany(group => group.GetProperty("vaultPaths").EnumerateArray().Select(p => p.GetString()!))
            .ToList();
        outside.Should().Contain("msp/bff-oidc").And.Contain("msp/postgres").And.NotContain("ai-stock-trading/moomoo");
        foreach (var path in outside)
            paths.Should().NotContain(p => p.EndsWith("/" + path, StringComparison.Ordinal));
    }

    // SC-22, IADR-0433 決定 4・5: role は BFF 専用 SA `bff` にだけ束縛し、ESO の role へ相乗りしない。
    [Fact]
    public void Bootstrap_binds_the_writer_role_to_the_bff_service_account_only()
    {
        var bootstrap = File.ReadAllText(BootstrapPath);
        var roleLine = bootstrap.Split('\n').Single(l => l.Contains("auth/kubernetes/role/bff-secret-writer", StringComparison.Ordinal));
        roleLine.Should().Contain("bound_service_account_names=bff ")
            .And.Contain("bound_service_account_namespaces=microservices-platform ")
            .And.Contain("policies=bff-secret-write ")
            .And.NotContain("default");

        bootstrap.Should().Contain("vault policy write bff-secret-write -' < \"$ROOT/deploy/local/vault/eso/policy-bff-secret-write.hcl\"");
        var esoLine = bootstrap.Split('\n').Single(l => l.Contains("auth/kubernetes/role/eso ", StringComparison.Ordinal));
        esoLine.Should().Contain("policies=eso-read ").And.NotContain("bff-secret-write");
    }
}

// リポジトリの実ファイルをテストから引く（出力ディレクトリから上へ辿る）。
internal static class RepoPaths
{
    public static string Resolve(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"リポジトリのファイルが見つからない: {relative}");
    }
}
