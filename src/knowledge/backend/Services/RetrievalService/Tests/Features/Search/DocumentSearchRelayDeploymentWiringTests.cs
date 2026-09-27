using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Tests.Features.Search;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0426]] 追記 1 (#1635):
// **正の配備ファイル（compose・helm・realm）で、AI 分析が east-west gRPC で名乗る client が retrieval-service の
// 信頼する中継者の集合（既定）に入っている**ことを固定する。
//
// 🔴 ずれると壊れ方が静か —— AI 分析の gRPC 検索が PERMISSION_DENIED になり、`GrpcRagSearchTransport` は
//   警告を出して**引用なしの回答**へ縮退する。例外もヘルスの赤も出ない（retrieval-service の警告ログだけ）。
//   集合を構成で変えるなら（`DocumentSearch__TrustedUserContextClients__N`）、AI 分析の client と揃えたうえでこの試験も直すこと。
[Trait("TestKind", "Unit")]
public class DocumentSearchRelayDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    private const string Realm = "deploy/keycloak/microservices-platform-realm.json";
    private const string TrustedKey = "DocumentSearch__TrustedUserContextClients";

    [Fact]
    public void 既定の信頼する中継者はaianalysis_serviceだけ()
    {
        DocumentSearchRelayOptions.DefaultTrustedUserContextClients.Should().Equal("aianalysis-service");
    }

    [Fact]
    public void compose_の_AI分析の_s2s_client_は既定の信頼する中継者に入り_gRPC検索を配線している()
    {
        var block = Block(ReadRepoFile(Compose), "services:", "  aianalysis-service:");
        var clientId = Regex.Match(block, @"(?m)^\s+ServiceToken__ClientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("aianalysis-service", "対照: AI 分析の s2s の client を読めていないなら以下は何も検査していない");
        DocumentSearchRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(@"(?m)^\s+Services__RetrievalServiceGrpc:\s*http://retrieval-service:8081\s*$",
            "AI 分析は gRPC で検索を呼ぶ（この client が DocumentSearch の中継者である根拠）");
        ReadRepoFile(Compose).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    [Fact]
    public void helm_の_AI分析の_s2s_client_は既定の信頼する中継者に入り_gRPC検索を配線している()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  aianalysis:");
        var clientId = Regex.Match(block, @"(?m)^    serviceToken:\s*\n\s+clientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("aianalysis-service", "対照: AI 分析の s2s の client を読めていないなら以下は何も検査していない");
        DocumentSearchRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(
            @"(?m)^\s+- name: Services__RetrievalServiceGrpc\s*\n\s+value:\s*""http://retrieval-service:8081""\s*$",
            "AI 分析は gRPC で検索を呼ぶ（この client が DocumentSearch の中継者である根拠）");
        ReadRepoFile(Helm).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    // realm に既定の client が機密クライアント（サービスアカウント有効）として在り、そのサービスアカウントが
    // `platform-service` を持つ（`ServiceCaller` を通れる）。名前がずれると陽性対照が本番で成り立たない。
    [Fact]
    public void realm_に既定の中継者の機密クライアントとplatform_serviceを持つサービスアカウントが在る()
    {
        using var realm = JsonDocument.Parse(ReadRepoFile(Realm));
        foreach (var clientId in DocumentSearchRelayOptions.DefaultTrustedUserContextClients)
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
