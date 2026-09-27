using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DocumentService.Features.Documents.AddTag;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, 計画 ADR-0086 決定 1, [[IADR-0410]] 追記 1 (#1636):
// **正の配備ファイル（compose・helm・realm）で、GraphService が east-west gRPC で名乗る client が document-service の
// 信頼する中継者の集合（既定）に入っている**ことを固定する。
//
// 🔴 ずれると壊れ方は「承認が 502」—— graph-service のタグ反映が PERMISSION_DENIED になり、`GrpcDocumentTagWriter` は
//   `Unavailable` へ倒して承認を確定しない（成功へは縮退しない）。集合を構成で変えるなら
//   （`DocumentTagWrite__TrustedUserContextClients__N`）、graph の client と揃えたうえでこの試験も直すこと。
[Trait("TestKind", "Unit")]
public class DocumentTagWriteRelayDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    private const string Realm = "deploy/keycloak/microservices-platform-realm.json";
    private const string TrustedKey = "DocumentTagWrite__TrustedUserContextClients";

    [Fact]
    public void 既定の信頼する中継者はgraph_serviceだけ()
    {
        DocumentTagWriteRelayOptions.DefaultTrustedUserContextClients.Should().Equal("graph-service");
    }

    [Fact]
    public void compose_の_graphの_s2s_client_は既定の信頼する中継者に入りタグ反映をgRPCで配線している()
    {
        var block = Block(ReadRepoFile(Compose), "services:", "  graph-service:");
        var clientId = Regex.Match(block, @"(?m)^\s+ServiceToken__ClientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("graph-service", "対照: graph の s2s の client を読めていないなら以下は何も検査していない");
        DocumentTagWriteRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(@"(?m)^\s+Services__DocumentServiceGrpc:\s*http://document-service:8081\s*$",
            "graph はタグ反映を gRPC で呼ぶ（この client が DocumentTagWrite の中継者である根拠）");
        ReadRepoFile(Compose).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    [Fact]
    public void helm_の_graphの_s2s_client_は既定の信頼する中継者に入りタグ反映をgRPCで配線している()
    {
        var block = Block(ReadRepoFile(Helm), "services:", "  graph:");
        var clientId = Regex.Match(block, @"(?m)^    serviceToken:\s*\n\s+clientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().Be("graph-service", "対照: graph の s2s の client を読めていないなら以下は何も検査していない");
        DocumentTagWriteRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        block.Should().MatchRegex(
            @"(?m)^\s+- name: Services__DocumentServiceGrpc\s*\n\s+value:\s*""http://document-service:8081""\s*$",
            "graph はタグ反映を gRPC で呼ぶ（この client が DocumentTagWrite の中継者である根拠）");
        ReadRepoFile(Helm).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    // realm に既定の client が機密クライアント（サービスアカウント有効）として在り、そのサービスアカウントが
    // `platform-service` を持つ（`ServiceCaller` を通れる）。名前がずれると陽性対照が本番で成り立たない。
    [Fact]
    public void realm_に既定の中継者の機密クライアントとplatform_serviceを持つサービスアカウントが在る()
    {
        using var realm = JsonDocument.Parse(ReadRepoFile(Realm));
        foreach (var clientId in DocumentTagWriteRelayOptions.DefaultTrustedUserContextClients)
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

    // FR-18, NFR-09, [[IADR-0410]] 追記 2 (#1636 段 2): 承認者が管理者かは認可サービスの `UserDirectory/CheckRealmRole` で引く。
    // 🔴 document-service に認可サービスの gRPC 宛先が配線されていないと縮退（常に「判定できない」）が選ばれ、管理者の承認が
    //   すべて UNAVAILABLE（graph は 502）になる。realm の document-service が `platform-service` を持たないと門で PERMISSION_DENIED になる。
    [Fact]
    public void compose_と_helm_の_documentは認可サービスのgRPC宛先を持ち_realmのdocument_serviceはplatform_serviceを持つ()
    {
        Block(ReadRepoFile(Compose), "services:", "  document-service:").Should().MatchRegex(
            @"(?m)^\s+Services__AuthorizationServiceGrpc:\s*http://authorization-service:8081\s*$");
        Block(ReadRepoFile(Helm), "services:", "  document:").Should().MatchRegex(
            @"(?m)^\s+- name: Services__AuthorizationServiceGrpc\s*\n\s+value:\s*""http://authorization-service:8081""\s*$");

        using var realm = JsonDocument.Parse(ReadRepoFile(Realm));
        realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out var s) && s.GetString() == "document-service")
            .GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString())
            .Should().Contain("platform-service");
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
