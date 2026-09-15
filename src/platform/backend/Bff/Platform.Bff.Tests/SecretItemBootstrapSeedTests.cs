using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Platform.Bff.Foundation.Secrets;

namespace Platform.Bff.Tests;

// SC-22, ADR-0095 決定 4, IADR-0456 決定 6 (#1477): **画面が書く KV を bootstrap が消さないこと**を bootstrap.sh の字面で固定する。
//
// 従前の bootstrap は SC-22 の KV を毎回 `vault kv put`（全置換）しており、`k8s-local-up.sh` を再実行するたびに
// 画面で入れた値が env の既定（空）で消えた。**items[] のパスへの put はすべて「無いときだけ」**（`vkv_exists` の分岐の中・`-cas=0`）。
// 加えて、AST の app-secrets の seed が契約（#1477）のキー集合と realm の dev 既定に一致すること、moomoo を seed しないことを固定する。
public class SecretItemBootstrapSeedTests
{
    private static readonly string CatalogPath = RepoPaths.Resolve("deploy/bootstrap/sc22-secret-items.json");
    private static readonly string BootstrapPath = RepoPaths.Resolve("deploy/local/vault/eso/bootstrap.sh");
    private static readonly string RealmPath = RepoPaths.Resolve("deploy/keycloak/microservices-platform-realm.json");

    // コメント行を落とし、行末の `\` による継続を 1 文へつなぐ（put の引数は複数行に折り返されている）。
    private static List<string> Statements()
    {
        var statements = new List<string>();
        var current = string.Empty;
        foreach (var raw in File.ReadAllLines(BootstrapPath))
        {
            if (current.Length == 0 && raw.TrimStart().StartsWith('#')) continue;
            var line = raw.TrimEnd();
            if (line.EndsWith('\\'))
            {
                current += line[..^1] + " ";
                continue;
            }

            statements.Add(current + line);
            current = string.Empty;
        }

        return statements;
    }

    private static Regex PutOn(string vaultPath) =>
        new($@"\bkv put\b[^\n]*?\bsecret/{Regex.Escape(vaultPath)}(?=[\s'""])");

    // SC-22, IADR-0456 決定 6: items[] のパスへの put は、すべて `vkv_exists <path>` が「無い」側の分岐の中にあり、`-cas=0` を持つ。
    [Fact]
    public void Kv_puts_on_screen_written_paths_are_create_only_and_guarded()
    {
        var catalog = SecretItemCatalog.Load(CatalogPath);
        var statements = Statements();
        var guardedPaths = new List<string>();

        foreach (var item in catalog.Items)
        {
            var pattern = PutOn(item.VaultPath);
            for (var i = 0; i < statements.Count; i++)
            {
                if (!pattern.IsMatch(statements[i])) continue;

                statements[i].Should().Contain("-cas=0", $"secret/{item.VaultPath} の作成は「無いときだけ」であること");

                // 直近の `if [!] vkv_exists <path>` を遡って探し、put がその「無い」側の分岐にあることを確かめる。
                var guard = Regex.Escape(item.VaultPath);
                var ifAt = statements.FindLastIndex(i, s => Regex.IsMatch(s, $@"^\s*if\s+(!\s*)?vkv_exists\s+{guard}\s*;\s*then\s*$"));
                ifAt.Should().BeGreaterThanOrEqualTo(0, $"secret/{item.VaultPath} の put が vkv_exists の分岐の中に無い（無条件の上書き）");
                var between = statements.Skip(ifAt + 1).Take(i - ifAt - 1).ToList();
                between.Should().NotContain(s => Regex.IsMatch(s, @"^\s*fi\s*$"), $"secret/{item.VaultPath} の put が分岐の外にある");

                var negated = Regex.IsMatch(statements[ifAt], @"^\s*if\s+!");
                var inElse = between.Any(s => Regex.IsMatch(s, @"^\s*else\s*$"));
                (negated ? !inElse : inElse).Should().BeTrue($"secret/{item.VaultPath} の put は「KV が無い」側の分岐にあること");
                guardedPaths.Add(item.VaultPath);
            }
        }

        // 陽性対照: seed する 4 つの KV の put を実際に見つけている（解析の取りこぼしで緑にならない）。
        guardedPaths.Distinct().Order(StringComparer.Ordinal).Should().Equal(
            "ai-stock-trading/app-secrets", "msp/keycloak-smtp", "msp/llm-provider-credentials", "msp/wikijs-sync");
    }

    // SC-22, IADR-0456 決定 6: 既に在る KV への部分更新は、値が空なら何もしない（未指定の env で画面の値を消さない）。
    [Fact]
    public void Patch_helper_does_nothing_for_empty_values()
    {
        var text = File.ReadAllText(BootstrapPath);
        var body = Regex.Match(text, @"(?s)vkv_patch_nonempty\(\)\s*\{(.*?)\n\}").Groups[1].Value;

        body.Should().NotBeEmpty("vkv_patch_nonempty の定義を読めること");
        var guard = body.IndexOf("[ -n \"$3\" ] || return 0", StringComparison.Ordinal);
        var patch = body.IndexOf("vault kv patch", StringComparison.Ordinal);
        guard.Should().BeGreaterThanOrEqualTo(0);
        patch.Should().BeGreaterThan(guard, "空の値を弾いてから部分更新すること");
        // 🔴 値はコマンドラインの引数に載せない（stdin で渡す）。
        body.Should().Contain("$2=-");
    }

    // SC-22, IADR-0456 決定 6: app-secrets の seed のキー集合は契約（items[] の書ける 12 ＋ notWritable 8）と一致し、
    // 書けるキーは空文字、*-auth-client-* は realm の機密クライアントと同値である。
    [Fact]
    public void App_secrets_seed_matches_the_contract_keys_and_realm_defaults()
    {
        var put = Statements().Single(s => PutOn("ai-stock-trading/app-secrets").IsMatch(s));
        var seeded = Regex.Matches(put, @"(?<key>[A-Za-z0-9._-]+)='(?<value>[^']*)'")
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);

        using var allowlist = JsonDocument.Parse(File.ReadAllText(CatalogPath));
        var item = allowlist.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("item").GetString() == "ast-app-secrets");
        var writable = SecretItemCatalog.Load(CatalogPath).Find("ast-app-secrets")!.Properties;
        var notWritable = item.GetProperty("notWritable").EnumerateArray().Select(e => e.GetString()!).ToList();

        writable.Should().HaveCount(12);
        notWritable.Should().HaveCount(8);
        seeded.Keys.Order(StringComparer.Ordinal).Should().Equal(writable.Concat(notWritable).Order(StringComparer.Ordinal));
        foreach (var key in writable)
            seeded[key].Should().BeEmpty($"{key} は画面から入れる値であり、seed は空文字であること");

        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath));
        var clients = realm.RootElement.GetProperty("clients").EnumerateArray()
            .Where(c => c.TryGetProperty("secret", out _))
            .ToDictionary(c => c.GetProperty("clientId").GetString()!, c => c.GetProperty("secret").GetString()!);
        foreach (var prefix in new[] { "service", "kb", "llm", "discord-owner" })
        {
            var clientId = seeded[$"{prefix}-auth-client-id"];
            clients.Should().ContainKey(clientId, $"{prefix} の client id は realm の機密クライアントであること");
            seeded[$"{prefix}-auth-client-secret"].Should().Be(clients[clientId], $"{prefix} の secret は realm と同値であること");
        }
    }

    // SC-22, IADR-0456 決定 6 (#1477 / PR #1478 監査 D4): app-secrets が既に在る（画面が先に 1 プロパティだけ書いて作った）ときも、
    // *-auth-client-* 8 件は**無いものだけ**、seed と同じ realm の値で足す。在る値は触らない（取得に成功したら patch しない）。
    [Fact]
    public void App_secrets_present_branch_fills_only_missing_auth_client_keys()
    {
        var statements = Statements();
        var ifAt = statements.FindIndex(s => Regex.IsMatch(s, @"^\s*if\s+!\s*vkv_exists\s+ai-stock-trading/app-secrets\s*;\s*then\s*$"));
        ifAt.Should().BeGreaterThanOrEqualTo(0, "app-secrets の seed の分岐を読めること");
        var fiAt = statements.FindIndex(ifAt, s => Regex.IsMatch(s, @"^\s*fi\s*$"));
        var elseAt = statements.FindIndex(ifAt, fiAt - ifAt, s => Regex.IsMatch(s, @"^\s*else\s*$"));
        elseAt.Should().BeGreaterThan(ifAt, "KV が在る側の分岐を持つこと");

        var filled = statements.Skip(elseAt + 1).Take(fiAt - elseAt - 1)
            .Select(s => Regex.Match(s, @"^\s*vkv_patch_if_missing\s+ai-stock-trading/app-secrets\s+(?<key>[A-Za-z0-9._-]+)\s+'(?<value>[^']*)'\s*$"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);

        var put = statements.Single(s => PutOn("ai-stock-trading/app-secrets").IsMatch(s));
        var seeded = Regex.Matches(put, @"(?<key>[A-Za-z0-9._-]+)='(?<value>[^']*)'")
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        using var allowlist = JsonDocument.Parse(File.ReadAllText(CatalogPath));
        var notWritable = allowlist.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("item").GetString() == "ast-app-secrets")
            .GetProperty("notWritable").EnumerateArray().Select(e => e.GetString()!).ToList();

        filled.Keys.Order(StringComparer.Ordinal).Should().Equal(notWritable.Order(StringComparer.Ordinal));
        foreach (var key in notWritable)
            filled[key].Should().Be(seeded[key], $"{key} は seed と同じ realm の値で足すこと");

        // 補助関数: 取得に成功したら何もしない（在る値を上書きしない）。取得の後でだけ patch する。
        var body = Regex.Match(File.ReadAllText(BootstrapPath), @"(?s)vkv_patch_if_missing\(\)\s*\{(.*?)\n\}").Groups[1].Value;
        body.Should().NotBeEmpty("vkv_patch_if_missing の定義を読めること");
        var get = body.IndexOf("vault kv get -field=$2", StringComparison.Ordinal);
        var patch = body.IndexOf("vault kv patch", StringComparison.Ordinal);
        get.Should().BeGreaterThanOrEqualTo(0);
        body.Should().Contain("&& return 0");
        patch.Should().BeGreaterThan(get, "在否を確かめてから足すこと");
    }

    // SC-22, IADR-0456 決定 6: moomoo / moomoo-rsa は seed しない（未設定のあいだ OpenD は Secret 不在で待機する＝fail-closed）。
    [Fact]
    public void Moomoo_kvs_are_not_seeded()
    {
        var statements = Statements();
        statements.Should().Contain(s => s.Contains("secret/ai-stock-trading/app-secrets", StringComparison.Ordinal), "陽性対照");
        statements.Should().NotContain(s => s.Contains("secret/ai-stock-trading/moomoo", StringComparison.Ordinal));
    }
}
