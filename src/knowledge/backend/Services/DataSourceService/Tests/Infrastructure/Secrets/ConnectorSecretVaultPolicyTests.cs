using System.Text.RegularExpressions;
using AwesomeAssertions;
using DataSourceService.Infrastructure.Secrets;

namespace DataSourceService.Tests.Infrastructure.Secrets;

// FR-01, NFR-18, [[IADR-0495]] 決定 1 (#458 段 S1): datasource-service の Vault 権限を**policy の字面で**固定する
// （BFF の `SecretItemVaultPolicyTests` と同型。統制は「実装が正しければ」ではなく字面で読めることにある。IADR-0433 §理由）。
//
// - policy の path 集合は専用接頭辞 `secret/data/datasource/*` の `read` だけ（多くも少なくもない）。
// - ESO の policy（`policy-eso-read.hcl`）はこの接頭辞を読めない。datasource の policy は `msp/*` も AST も読めない。
// - role は datasource-service 専用 SA にだけ束縛し、ESO の role へ相乗りしない。helm の SA 名・role 名と一致する。
[Trait("TestKind", "Unit")]
public sealed class ConnectorSecretVaultPolicyTests
{
    private const string DedicatedGlob = "secret/data/datasource/*";

    private static readonly string PolicyPath = RepoFile.Resolve("deploy/local/vault/eso/policy-datasource-connector-read.hcl");
    private static readonly string EsoPolicyPath = RepoFile.Resolve("deploy/local/vault/eso/policy-eso-read.hcl");
    private static readonly string BffPolicyPath = RepoFile.Resolve("deploy/local/vault/eso/policy-bff-secret-write.hcl");
    private static readonly string BootstrapPath = RepoFile.Resolve("deploy/local/vault/eso/bootstrap.sh");
    private static readonly string ValuesPath = RepoFile.Resolve("deploy/helm/microservices-platform/values.yaml");

    private static readonly Regex PathBlock = new(
        """path\s+"(?<path>[^"]+)"\s*\{\s*capabilities\s*=\s*\[(?<caps>[^\]]*)\]\s*\}""",
        RegexOptions.Compiled);

    private static Dictionary<string, string[]> ParsePolicy(string file)
    {
        var text = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith('#')));
        var blocks = PathBlock.Matches(text);
        // 読めなかった path ブロックを黙って落とさない（解析器の取りこぼしで緑にならないように数を合わせる）。
        Regex.Matches(text, @"\bpath\s+""").Count.Should().Be(blocks.Count, $"{file} のすべての path ブロックを解析できること");
        return blocks.ToDictionary(
            m => m.Groups["path"].Value,
            m => m.Groups["caps"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim('"')).Order(StringComparer.Ordinal).ToArray());
    }

    // Vault の policy の path の照合（末尾の `*` は接頭辞一致、`+` は 1 セグメント）。
    internal static bool PolicyPathMatches(string policyPath, string requestPath)
    {
        var glob = policyPath.EndsWith('*');
        var pattern = glob ? policyPath[..^1] : policyPath;
        var regex = "^" + string.Join("/", pattern.Split('/').Select(s => s == "+" ? "[^/]+" : Regex.Escape(s)))
                    + (glob ? "" : "$");
        return Regex.IsMatch(requestPath, regex);
    }

    [Fact]
    public void DatasourcePolicy_IsExactlyReadOnTheDedicatedPrefix()
    {
        var policy = ParsePolicy(PolicyPath);

        policy.Keys.Should().Equal([DedicatedGlob]);
        policy[DedicatedGlob].Should().Equal(["read"]);
    }

    // 解決器が送る要求のパスと policy の接頭辞が対である（片方だけ動かすと 403 か、接頭辞の外を引く）。
    [Fact]
    public void ResolverPrefix_AndKvMount_MatchThePolicy()
    {
        var mount = new VaultConnectorSecretOptions().KvMount;
        var request = $"{mount}/data/{VaultConnectorSecretResolver.PathPrefix}ds-1";

        PolicyPathMatches(DedicatedGlob, request).Should().BeTrue();
        DedicatedGlob.Should().Be($"{mount}/data/{VaultConnectorSecretResolver.PathPrefix}*");
    }

    // 🔴 ESO の policy はこの接頭辞を読めない（読めると、コネクタの資格情報が k8s Secret へ材料化され得る）。
    [Theory]
    [InlineData("secret/data/datasource/ds-1")]
    [InlineData("secret/data/datasource/nested/ds-2")]
    [InlineData("secret/metadata/datasource/ds-1")]
    public void EsoPolicy_CannotReadTheDedicatedPrefix(string request)
    {
        var eso = ParsePolicy(EsoPolicyPath);
        // 陽性対照: 照合器が ESO の既存の読み取りを当てられること。
        eso.Keys.Should().Contain(p => PolicyPathMatches(p, "secret/data/msp/postgres"));

        eso.Keys.Should().NotContain(p => PolicyPathMatches(p, request));
    }

    // 🔴 datasource の policy は基盤（msp/*）・AST・ESO の読む先を読めない。
    [Theory]
    [InlineData("secret/data/msp/postgres")]
    [InlineData("secret/data/msp/datasource-service-token")]
    [InlineData("secret/data/ai-stock-trading/app-secrets")]
    [InlineData("secret/data/datasourcex/ds-1")]
    [InlineData("secret/metadata/datasource/ds-1")]
    [InlineData("secret/data/datasource")]
    public void DatasourcePolicy_CannotReadOutsideTheDedicatedPrefix(string request)
    {
        ParsePolicy(PolicyPath).Keys.Should().NotContain(p => PolicyPathMatches(p, request));
    }

    // BFF の書き込み policy もこの接頭辞に触れない（書き手は段 S2 以降で決める。planning#716）。
    [Fact]
    public void BffWritePolicy_DoesNotCoverTheDedicatedPrefix()
    {
        ParsePolicy(BffPolicyPath).Keys.Should().NotContain(p => PolicyPathMatches(p, "secret/data/datasource/ds-1"));
    }

    [Fact]
    public void Bootstrap_BindsTheReaderRoleToTheDatasourceServiceAccountOnly()
    {
        var bootstrap = File.ReadAllText(BootstrapPath);
        var roleLine = bootstrap.Split('\n').Single(l => l.Contains("auth/kubernetes/role/datasource-connector-reader", StringComparison.Ordinal));
        roleLine.Should().Contain("bound_service_account_names=datasource-service ")
            .And.Contain("bound_service_account_namespaces=microservices-platform ")
            .And.Contain("policies=datasource-connector-read ")
            .And.NotContain("default");

        bootstrap.Should().Contain(
            "vault policy write datasource-connector-read -' < \"$ROOT/deploy/local/vault/eso/policy-datasource-connector-read.hcl\"");

        var esoLine = bootstrap.Split('\n').Single(l => l.Contains("auth/kubernetes/role/eso ", StringComparison.Ordinal));
        esoLine.Should().Contain("policies=eso-read ").And.NotContain("datasource-connector-read");
        var bffLine = bootstrap.Split('\n').Single(l => l.Contains("auth/kubernetes/role/bff-secret-writer", StringComparison.Ordinal));
        bffLine.Should().NotContain("datasource-connector-read");
    }

    // helm が作る SA の名前・role 名・既定の address（空＝Vault なし）が bootstrap と解決器の既定と一致する。
    [Fact]
    public void HelmValues_DeclareTheDedicatedServiceAccount_RoleAndEmptyAddressByDefault()
    {
        var lines = File.ReadAllLines(ValuesPath);
        var start = Array.FindIndex(lines, l => l == "  datasource:");
        start.Should().BePositive();
        var end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^  [a-z][a-zA-Z]*:\s*$"));
        var block = string.Join('\n', lines[start..end]);

        Regex.IsMatch(block, @"\n    serviceAccount:\n      create: true\n      name: datasource-service\n").Should().BeTrue(block);
        Regex.IsMatch(block, @"\n    vault:\n      address: """"\n      role: datasource-connector-reader\n").Should().BeTrue(block);
        new VaultConnectorSecretOptions().Role.Should().Be("datasource-connector-reader");
    }
}

// リポジトリの実ファイルをテストから引く（出力ディレクトリから上へ辿る）。
internal static class RepoFile
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
