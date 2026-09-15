using System.Text.RegularExpressions;
using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0456 決定 4 (#1477):
// **BFF が force-sync の注釈を付けられる ExternalSecret の集合が、allowlist（`items[].externalSecret`）と完全一致すること**を
// RBAC の字面で固定する（SecretItemVaultPolicyTests と同じ考え方。統制は「BFF が正しく実装されていれば」ではなく字面で読める形に置く）。
//
// 読む字面は 2 つ: チャートの Role（MSP の名前空間）と deploy/local/vault/eso/ の Role（platform-infra / ai-stock-trading）。
// 🔴 **名前が 1 つ多くても少なくても落ちる。** verbs は get / patch だけ、束縛先は SA `bff` だけ。
public class SecretItemExternalSecretRbacTests
{
    private static readonly string CatalogPath = RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json");
    private static readonly string HelmRbacPath = RepoPaths.Resolve("deploy/helm/microservices-platform/templates/bff-externalsecret-sync-rbac.yaml");
    private static readonly string HelmValuesPath = RepoPaths.Resolve("deploy/helm/microservices-platform/values.yaml");
    private static readonly string LocalRbacPath = RepoPaths.Resolve("deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml");

    private const string ChartNamespaceExpression = "{{ .Values.namespace.name }}";
    private const string ChartServiceAccountExpression = "{{ $bff.serviceAccount.name }}";

    private sealed record RbacDocument(
        string Kind,
        string Name,
        string Namespace,
        IReadOnlyList<string> ApiGroups,
        IReadOnlyList<string> Resources,
        IReadOnlyList<string> Verbs,
        IReadOnlyList<string> ResourceNames,
        string? RoleRef,
        IReadOnlyList<(string Kind, string Name, string Namespace)> Subjects);

    private static string ChartNamespace()
    {
        var match = Regex.Match(File.ReadAllText(HelmValuesPath), @"(?m)^namespace:\s*\r?\n(?:[ \t]+.*\r?\n)*?[ \t]+name:\s*(\S+)");
        match.Success.Should().BeTrue("values.yaml の namespace.name を読めること");
        return match.Groups[1].Value;
    }

    private static string ChartServiceAccount()
    {
        var match = Regex.Match(File.ReadAllText(HelmValuesPath),
            @"(?m)^  bff:\s*\r?\n(?:(?:    .*|\s*)\r?\n)*?    serviceAccount:\s*\r?\n\s+create:\s*true\s*\r?\n\s+name:\s*(\S+)");
        match.Success.Should().BeTrue("values.yaml の services.bff.serviceAccount.name を読めること");
        return match.Groups[1].Value;
    }

    // 最小の YAML 読み: 文書を `---` で割り、コメント・テンプレートの制御行を落とし、使う欄だけを拾う。
    // 🔴 拾えなかった Role / RoleBinding を黙って落とさない（文書数を数えて合わせる）。
    private static List<RbacDocument> Parse(string path, string chartNamespace, string chartServiceAccount)
    {
        var text = Regex.Replace(File.ReadAllText(path), @"\{\{-?\s*/\*.*?\*/\s*-?\}\}", "", RegexOptions.Singleline);
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !l.TrimStart().StartsWith('#') && !l.TrimStart().StartsWith("{{", StringComparison.Ordinal))
            .ToList();

        var documents = new List<List<string>> { new() };
        foreach (var line in lines)
        {
            if (line.Trim() == "---") documents.Add([]);
            else documents[^1].Add(line);
        }

        string Resolve(string value) => value
            .Replace(ChartNamespaceExpression, chartNamespace, StringComparison.Ordinal)
            .Replace(ChartServiceAccountExpression, chartServiceAccount, StringComparison.Ordinal)
            .Trim().Trim('"');

        static List<string> Flow(string body, string key)
        {
            var m = Regex.Match(body, $@"(?m)^\s*-?\s*{key}:\s*\[([^\]]*)\]");
            return m.Success
                ? [.. m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => v.Trim('"'))]
                : [];
        }

        var result = new List<RbacDocument>();
        foreach (var document in documents.Where(d => d.Any(l => l.StartsWith("kind:", StringComparison.Ordinal))))
        {
            var body = string.Join('\n', document);
            var kind = Regex.Match(body, @"(?m)^kind:\s*(\S+)").Groups[1].Value;
            var name = Resolve(Regex.Match(body, @"(?m)^metadata:\s*\n(?:\s+.*\n)*?\s+name:\s*(.+)$").Groups[1].Value);
            var ns = Resolve(Regex.Match(body, @"(?m)^metadata:\s*\n(?:\s+.*\n)*?\s+namespace:\s*(.+)$").Groups[1].Value);

            var resourceNames = new List<string>();
            var at = document.FindIndex(l => Regex.IsMatch(l, @"^\s+resourceNames:\s*$"));
            if (at >= 0)
            {
                for (var i = at + 1; i < document.Count && Regex.IsMatch(document[i], @"^\s+-\s+\S+\s*$"); i++)
                    resourceNames.Add(Resolve(Regex.Match(document[i], @"-\s+(\S+)").Groups[1].Value));
            }

            var subjects = Regex.Matches(body, @"(?m)^\s*-\s*kind:\s*(\S+)\s*\n\s+name:\s*(.+)\n\s+namespace:\s*(.+)$")
                .Select(m => (m.Groups[1].Value, Resolve(m.Groups[2].Value), Resolve(m.Groups[3].Value)))
                .ToList();
            var roleRef = Regex.Match(body, @"(?m)^roleRef:\s*\n(?:\s+.*\n)*?\s+name:\s*(.+)$");

            result.Add(new RbacDocument(kind, name, ns, Flow(body, "apiGroups"), Flow(body, "resources"), Flow(body, "verbs"),
                resourceNames, roleRef.Success ? Resolve(roleRef.Groups[1].Value) : null, subjects));
        }

        Regex.Matches(text, @"(?m)^kind:\s*(Role|RoleBinding|ClusterRole|ClusterRoleBinding)\s*$").Count
            .Should().Be(result.Count, $"{path} のすべての RBAC 文書を解析できること");
        return result;
    }

    private static List<RbacDocument> AllDocuments()
    {
        var chartNamespace = ChartNamespace();
        var serviceAccount = ChartServiceAccount();
        return [.. Parse(HelmRbacPath, chartNamespace, serviceAccount), .. Parse(LocalRbacPath, chartNamespace, serviceAccount)];
    }

    // SC-22, IADR-0456 決定 4: 名前空間ごとの resourceNames が items[] の externalSecret と完全一致する（多くも少なくもない）。
    [Fact]
    public void Role_resource_names_equal_the_allowlist_external_secrets_per_namespace()
    {
        var catalog = SecretItemCatalog.Load(CatalogPath);
        var expected = catalog.Items
            .GroupBy(i => i.ExternalSecret.Namespace)
            .ToDictionary(g => g.Key, g => g.Select(i => i.ExternalSecret.Name).Order(StringComparer.Ordinal).ToList());

        var roles = AllDocuments().Where(d => d.Kind == "Role").ToList();
        roles.GroupBy(r => r.Namespace).Should().OnlyContain(g => g.Count() == 1, "名前空間ごとに Role は 1 つ");
        var actual = roles.ToDictionary(r => r.Namespace, r => r.ResourceNames.Order(StringComparer.Ordinal).ToList());

        actual.Keys.Order(StringComparer.Ordinal).Should().Equal(expected.Keys.Order(StringComparer.Ordinal));
        foreach (var (ns, names) in expected)
            actual[ns].Should().Equal(names, $"{ns} の resourceNames は items[] の externalSecret と一致すること");
        // 陽性対照: 3 つの名前空間にまたがり、MSP の名前空間はチャートが持つ。
        expected.Keys.Should().BeEquivalentTo(["microservices-platform", "platform-infra", "ai-stock-trading"]);
    }

    // SC-22, IADR-0456 決定 4: 権限は ExternalSecret の get / patch だけ（Secret・ワイルドカード・ClusterRole を含まない）。
    [Fact]
    public void Roles_grant_only_get_and_patch_on_external_secrets()
    {
        var documents = AllDocuments();
        documents.Should().NotContain(d => d.Kind.StartsWith("Cluster", StringComparison.Ordinal));
        foreach (var role in documents.Where(d => d.Kind == "Role"))
        {
            role.Name.Should().Be("bff-externalsecret-sync");
            role.ApiGroups.Should().Equal("external-secrets.io");
            role.Resources.Should().Equal("externalsecrets");
            role.Verbs.Order(StringComparer.Ordinal).Should().Equal("get", "patch");
            role.ResourceNames.Should().NotBeEmpty("resourceNames の無い Role は名前空間の全 ExternalSecret に効く");
        }

        foreach (var path in new[] { HelmRbacPath, LocalRbacPath })
        {
            var code = string.Join('\n', File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('#')));
            code.Should().NotContain("\"*\"");
            code.Should().NotContain("\"secrets\"", "Secret（値）への権限を与えない");
        }
    }

    // SC-22, IADR-0456 決定 4, IADR-0433 決定 4: 束縛先は BFF 専用の SA `microservices-platform/bff` だけ（`default` に束縛しない）。
    [Fact]
    public void Role_bindings_bind_only_the_bff_service_account()
    {
        var documents = AllDocuments();
        var roleNamespaces = documents.Where(d => d.Kind == "Role").Select(d => d.Namespace).Order(StringComparer.Ordinal).ToList();
        var bindings = documents.Where(d => d.Kind == "RoleBinding").ToList();

        bindings.Select(b => b.Namespace).Order(StringComparer.Ordinal).Should().Equal(roleNamespaces, "Role ごとに RoleBinding が 1 つ");
        foreach (var binding in bindings)
        {
            binding.RoleRef.Should().Be("bff-externalsecret-sync");
            binding.Subjects.Should().Equal(("ServiceAccount", "bff", "microservices-platform"));
        }
    }

    // SC-22, IADR-0456 決定 4: MSP / platform-infra の同期先 ExternalSecret は deploy/local/vault/eso/ に実在する
    // （名前の書き違いで「権限はあるのに依頼が 404」になるのを防ぐ）。ai-stock-trading の 3 つは AST のチャートが作る（AST#795）。
    [Fact]
    public void Platform_owned_external_secrets_exist_as_manifests()
    {
        var catalog = SecretItemCatalog.Load(CatalogPath);
        var directory = Path.GetDirectoryName(LocalRbacPath)!;
        var manifests = Directory.GetFiles(directory, "externalsecret-*.yaml")
            .Select(File.ReadAllText)
            .Select(t => (
                Name: Regex.Match(t, @"(?m)^metadata:\s*\r?\n(?:\s+.*\r?\n)*?\s+name:\s*(\S+)").Groups[1].Value,
                Namespace: Regex.Match(t, @"(?m)^metadata:\s*\r?\n(?:\s+.*\r?\n)*?\s+namespace:\s*(\S+)").Groups[1].Value))
            .ToList();
        manifests.Should().NotBeEmpty();

        var owned = catalog.Items.Select(i => i.ExternalSecret).Where(e => e.Namespace != "ai-stock-trading").ToList();
        owned.Should().HaveCount(3);
        foreach (var externalSecret in owned)
            manifests.Should().Contain((externalSecret.Name, externalSecret.Namespace));
    }
}
