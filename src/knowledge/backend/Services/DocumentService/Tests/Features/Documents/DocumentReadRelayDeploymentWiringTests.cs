using System.Text.RegularExpressions;
using AwesomeAssertions;
using DocumentService.Features.Documents;

namespace DocumentService.Tests.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0476]] 追記 (#1628):
// **正の配備ファイル（compose・helm）で、BFF が east-west gRPC で名乗る client が document-service の信頼する中継者の集合に入っている**
// ことを固定する。
//
// 🔴 ずれると壊れ方が静か —— BFF の gRPC の読み取りが利用者文脈つきで PERMISSION_DENIED になり、BFF は呼び出し箇所ごとの縮退
//   （一覧は空・詳細は 404）へ落ちる。例外もヘルスの赤も出ない（document-service の警告ログだけ）。
//   集合を構成で変えるなら（`DocumentRead__TrustedUserContextClients__N`）、BFF の client と揃えたうえでこの試験も直すこと。
[Trait("TestKind", "Unit")]
public class DocumentReadRelayDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    private const string TrustedKey = "DocumentRead__TrustedUserContextClients";

    [Fact]
    public void compose_の_BFF_の_s2s_client_は既定の信頼する中継者に入る()
    {
        var bff = Block(ReadRepoFile(Compose), "services:", "  bff:");
        var clientId = Regex.Match(bff, @"(?m)^\s+ServiceToken__ClientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().NotBeEmpty("対照: BFF の s2s の client を読めていないなら以下は何も検査していない");
        DocumentReadRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        ReadRepoFile(Compose).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
    }

    [Fact]
    public void helm_の_BFF_の_s2s_client_は既定の信頼する中継者に入る()
    {
        var bff = Block(ReadRepoFile(Helm), "services:", "  bff:");
        var clientId = Regex.Match(bff, @"(?m)^    serviceToken:\s*\n\s+clientId:\s*""?([^""\s]+)""?\s*$").Groups[1].Value;

        clientId.Should().NotBeEmpty("対照: BFF の s2s の client を読めていないなら以下は何も検査していない");
        DocumentReadRelayOptions.DefaultTrustedUserContextClients.Should().Contain(clientId);
        ReadRepoFile(Helm).Should().NotContain(TrustedKey, "構成で集合を変えるならこの試験を直す（上の 🔴）");
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
