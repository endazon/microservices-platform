using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DocumentService.Features.McpTools.Execute;

namespace DocumentService.Tests.Features.McpTools.Execute;

// FR-16, NFR-09, 計画 ADR-0117 決定 3, [[IADR-0479]]（2026-09-28 追記 / #1611 段 2）: **正の配備ファイル（compose・helm・realm）で、MCP サーバーが east-west gRPC で
// 名乗る client が実行口の信頼する中継者の集合（既定）に入り、実行の宛先に document-service の h2c が在る**ことを固定する（X-67）。
//
// 🔴 ずれると壊れ方が静か —— MCP のツール実行が PERMISSION_DENIED になり、MCP サーバーは利用者へ「実行できません」を返すだけ
//   （例外もヘルスの赤も出ない）。集合を構成で変えるなら（`McpToolExecution__TrustedUserContextClients__N`）、
//   MCP サーバーの client と揃えたうえでこの試験も直すこと。
[Trait("TestKind", "Unit")]
public class McpToolExecutionRelayDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    private const string Realm = "deploy/keycloak/microservices-platform-realm.json";
    private const string TrustedKey = "McpToolExecution__TrustedUserContextClients";

    [Fact]
    public void compose_のMCPサーバーのs2s_clientは既定の信頼する中継者に入り実行の宛先を配線している()
    {
        var block = Block(ReadRepoFile(Compose), "services:", "  mcp-service:");
        var clientId = Regex.Match(block, @"(?m)^\s+ServiceToken__ClientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("mcp-server", "対照: MCP サーバーの s2s の client を読めていないなら以下は何も検査していない");
        McpToolExecutionRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(@"(?m)^\s+Mcp__Services__document-service:\s*http://document-service:8081\s*$",
            "MCP サーバーはツールの実行を申告元の h2c へ送る（この client が実行口の中継者である根拠）");
        ReadRepoFile(Compose).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    [Fact]
    public void helm_のMCPサーバーのs2s_clientは既定の信頼する中継者に入り実行の宛先を配線している()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  mcp:");
        var clientId = Regex.Match(block, @"(?m)^    serviceToken:\s*\n\s+clientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("mcp-server", "対照: MCP サーバーの s2s の client を読めていないなら以下は何も検査していない");
        McpToolExecutionRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(
            @"(?m)^\s+- name: Mcp__Services__document-service\s*\n\s+value:\s*""http://document-service:8081""\s*$",
            "MCP サーバーはツールの実行を申告元の h2c へ送る");
        ReadRepoFile(Helm).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    // realm に既定の client が機密クライアント（サービスアカウント有効）として在り、そのサービスアカウントが
    // `platform-service` を持つ（`ServiceCaller` を通れる）。名前がずれると陽性対照が本番で成り立たない。
    [Fact]
    public void realm_に既定の中継者の機密クライアントとplatform_serviceを持つサービスアカウントが在る()
    {
        using var realm = JsonDocument.Parse(ReadRepoFile(Realm));
        foreach (var clientId in McpToolExecutionRelayOptions.DefaultTrustedUserContextClients)
        {
            var client = realm.RootElement.GetProperty("clients").EnumerateArray()
                .Single(c => c.GetProperty("clientId").GetString() == clientId);
            client.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeTrue(clientId);
            client.GetProperty("publicClient").GetBoolean().Should().BeFalse(clientId);

            var serviceAccount = realm.RootElement.GetProperty("users").EnumerateArray()
                .Single(u => u.TryGetProperty("serviceAccountClientId", out var s) && s.GetString() == clientId);
            serviceAccount.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString())
                .Should().Contain("platform-service", clientId);
        }
    }

    // `section` の行より後で最初に現れる `header` から、同じ字下げ以下の行が来るまでを返す（改行は \n に揃える）。
    private static string Block(string text, string section, string header)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sectionAt = Array.IndexOf(lines, section);
        sectionAt.Should().BeGreaterThanOrEqualTo(0, $"'{section}' が見つからない");
        var start = Array.IndexOf(lines, header, sectionAt + 1);
        start.Should().BeGreaterThan(sectionAt, $"'{section}' の下に '{header.Trim()}' が見つからない");
        var indent = header.Length - header.TrimStart().Length;
        var end = start + 1;
        while (end < lines.Length)
        {
            var l = lines[end];
            var lineIndent = l.Length - l.TrimStart().Length;
            if (l.Trim().Length > 0 && !l.TrimStart().StartsWith('#') && lineIndent <= indent)
                break;
            end++;
        }
        return string.Join('\n', lines[start..end]);
    }

    private static string ReadRepoFile(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    // 解決できなければ止める（fail-closed）。読めなかったファイルを空として扱うと何も検査しないまま緑になる。
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("リポジトリのルート（deploy/docker-compose.yml を持つ）が見つからない。");
    }
}
