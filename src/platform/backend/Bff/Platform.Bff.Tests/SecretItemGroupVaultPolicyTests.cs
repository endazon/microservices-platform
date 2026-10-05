using System.Text.RegularExpressions;
using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, NFR-18, 計画 ADR-0126 決定 2（ADR-0095 決定 3 の補完）, IADR-0501 決定 2 (#458 段 S2):
// **群の Vault policy の字面が `groups[]` と完全一致すること**を固定する（`SecretItemVaultPolicyTests` と同型）。
//
// 🔴 群の射程の「権限の層」はこの字面だけが担う（登録済みの ID は BFF のコードの検査）。
//   - path の集合は `groups[]` の接頭辞ごとの `<接頭辞>/+`（data）と metadata の 2 本ずつ（多くも少なくもない）。
//   - data は create・patch だけ、metadata は read だけ。`*`・list・delete・destroy・sudo は無い。
//   - 基盤の秘密（`msp/*`）・AST・項目ごとの policy の path へは届かない。
//   - role は既存の `bff-secret-writer`（SA `bff`）に並べて付き、ESO の role へ相乗りしない。
public class SecretItemGroupVaultPolicyTests
{
    private static readonly string PolicyPath = RepoPaths.Resolve("deploy/local/vault/eso/policy-bff-secret-group-write.hcl");
    private static readonly string ItemPolicyPath = RepoPaths.Resolve("deploy/local/vault/eso/policy-bff-secret-write.hcl");
    private static readonly string CatalogPath = RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json");
    private static readonly string BootstrapPath = RepoPaths.Resolve("deploy/local/vault/eso/bootstrap.sh");

    private static readonly Regex PathBlock = new(
        """path\s+"(?<path>[^"]+)"\s*\{\s*capabilities\s*=\s*\[(?<caps>[^\]]*)\]\s*\}""",
        RegexOptions.Compiled);

    private static string WithoutComments(string file) =>
        string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith('#')));

    private static Dictionary<string, string[]> ParsePolicy(string file)
    {
        var text = WithoutComments(file);
        var blocks = PathBlock.Matches(text);
        Regex.Matches(text, @"\bpath\s+""").Count.Should().Be(blocks.Count, "すべての path ブロックを解析できること");
        return blocks.ToDictionary(
            m => m.Groups["path"].Value,
            m => m.Groups["caps"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim('"')).Order(StringComparer.Ordinal).ToArray());
    }

    // Vault の policy の path の照合（末尾の `*` は接頭辞一致、`+` は 1 セグメント）。
    private static bool Matches(string policyPath, string requestPath)
    {
        var glob = policyPath.EndsWith('*');
        var pattern = glob ? policyPath[..^1] : policyPath;
        var regex = "^" + string.Join("/", pattern.Split('/').Select(s => s == "+" ? "[^/]+" : Regex.Escape(s)))
                    + (glob ? "" : "$");
        return Regex.IsMatch(requestPath, regex);
    }

    // ADR-0126 決定 2: path の集合は groups[] の接頭辞の `+`（data ＋ metadata）と完全一致する。
    [Fact]
    public void Policy_paths_equal_the_group_prefixes_exactly()
    {
        var catalog = SecretItemCatalog.Load(CatalogPath);
        catalog.Groups.Should().NotBeEmpty("陽性対照: 群が宣言されていること");
        var expected = catalog.Groups
            .SelectMany(g => new[] { $"{catalog.VaultMount}/data/{g.VaultPathPrefix}/+", $"{catalog.VaultMount}/metadata/{g.VaultPathPrefix}/+" })
            .Order(StringComparer.Ordinal);

        ParsePolicy(PolicyPath).Keys.Order(StringComparer.Ordinal).Should().Equal(expected);
    }

    // ADR-0126 決定 2: data は create・patch だけ（read・update なし）、metadata は read だけ。
    [Fact]
    public void Policy_grants_write_without_read_on_data_and_read_only_on_metadata()
    {
        foreach (var (path, caps) in ParsePolicy(PolicyPath))
        {
            if (path.Contains("/data/", StringComparison.Ordinal))
                caps.Should().Equal(["create", "patch"], $"{path} は値を読み返せず、KV を全置換できない形であること");
            else
                caps.Should().Equal(["read"], $"{path} は版と時刻だけを読めること");
        }
    }

    // ADR-0126 決定 2: `*`（接頭辞一致）と広い権限を含まない。`+` だけが許される（1 セグメント）。
    [Fact]
    public void Policy_uses_only_single_segment_wildcards_and_no_broad_capabilities()
    {
        var text = WithoutComments(PolicyPath);
        text.Should().NotContain("*");
        foreach (var forbidden in new[] { "\"list\"", "\"delete\"", "\"destroy\"", "\"sudo\"", "\"deny\"", "\"update\"", "\"read\", \"create\"" })
            text.Should().NotContain(forbidden);
    }

    // 🔴 群の policy は、基盤・AST の秘密と、項目ごとの policy の path へ届かない（陽性対照: 群のパスには届く）。
    [Fact]
    public void Policy_reaches_only_one_segment_under_the_group_prefix()
    {
        var paths = ParsePolicy(PolicyPath).Keys.ToList();
        paths.Should().Contain(p => Matches(p, "secret/data/datasource/3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

        var outside = ParsePolicy(ItemPolicyPath).Keys
            .Concat(["secret/data/msp/postgres", "secret/data/datasource/a/b", "secret/data/datasource", "secret/data/datasourcex/a"]);
        foreach (var request in outside)
            paths.Should().NotContain(p => Matches(p, request), $"{request} へ届かないこと");
    }

    // IADR-0501 決定 2: role は既存の `bff-secret-writer`（SA `bff`）に並べて付ける。ESO の role には付けない。
    [Fact]
    public void Bootstrap_attaches_the_group_policy_to_the_bff_writer_role_only()
    {
        var bootstrap = File.ReadAllText(BootstrapPath);
        bootstrap.Should().Contain(
            "vault policy write bff-secret-group-write -' < \"$ROOT/deploy/local/vault/eso/policy-bff-secret-group-write.hcl\"");

        var roles = bootstrap.Split('\n').Where(l => l.Contains("auth/kubernetes/role/", StringComparison.Ordinal)).ToList();
        roles.Where(l => l.Contains("bff-secret-group-write", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain("auth/kubernetes/role/bff-secret-writer ").And.Contain("bound_service_account_names=bff ");
    }
}
