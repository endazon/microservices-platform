using System.Text.RegularExpressions;
using FluentAssertions;

namespace KnowledgePlatform.IntegrationTests.Deployment;

// IADR-0017（Superseded by IADR-0024） / #62 / #100, FR-05, NFR(機密性), ADR-0005:
// ネットワーク分離はもはや「第一防御」ではない（第一防御は Istio STRICT mTLS。MeshMtlsTests 参照）。
// IADR-0024 により、docker-compose（ローカル開発ランタイム）の host 非公開は「多層防御
// （defense-in-depth）」として維持する。外部からの入口は引き続き BFF に一本化する。
// 本テストは compose のホスト公開ポートの回帰（内部サービスの再公開）を多層防御として防ぐ。
[Trait("Category", "Deployment")]
public sealed class NetworkIsolationTests
{
    // IADR-0017: host 公開を許すのはエッジ(BFF)と、開発利便のためのインフラ系のみ。
    // 下記のアプリ内部サービスは host `ports:` を公開してはならない（expose のみ）。
    private static readonly string[] InternalAppServices =
    [
        "document-service",
        "datasource-service",
        "retrieval-service",   // /search の ABAC は #55 で別管理だが host 公開停止は一律適用
        "aianalysis-service",
        "authorization-service",
        "wiki-service",
        "llm-gateway",
        "feedback-service",
        "dashboard-service",
    ];

    [Fact]
    public void InternalServices_MustNotPublishHostPorts()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        foreach (var svc in InternalAppServices)
        {
            blocks.Should().ContainKey(svc,
                $"'{svc}' が docker-compose.yml に存在すること");

            // IADR-0017: 内部サービスは host 公開（ports:）せず expose のみ。
            blocks[svc].Should().NotMatchRegex(@"(?m)^\s*ports:\s*$",
                $"IADR-0017: 内部サービス '{svc}' は host ポートを公開してはならない（expose を用いる）");
        }
    }

    [Fact]
    public void Bff_RemainsTheOnlyPublishedAppEdge()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        // エッジ(BFF)は外部からの唯一の入口として host 公開を維持する。
        blocks.Should().ContainKey("bff");
        blocks["bff"].Should().MatchRegex(@"(?m)^\s*ports:\s*$",
            "BFF は外部からの入口として host 公開を維持する");
        blocks["bff"].Should().Contain("5000:8080");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadComposeFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "docker-compose.yml");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("deploy/docker-compose.yml をリポジトリルートから解決できませんでした。");
    }

    // `services:` 配下の各サービス（2 スペースインデントの `name:`）を、次のサービスまでの本文へ分割する。
    private static Dictionary<string, string> SplitServiceBlocks(string compose)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = compose.Replace("\r\n", "\n").Split('\n');

        // トップレベル `services:` 以降を対象にする。
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^services:\s*$"));
        if (start < 0) return result;

        // サービス名は "  name:"（2 スペース）で始まる。volumes: 等のトップレベルキーで終端する。
        var header = new Regex(@"^  (?<name>[a-z0-9-]+):\s*$");
        string? current = null;
        var buf = new List<string>();

        void Flush()
        {
            if (current is not null)
                result[current] = string.Join("\n", buf);
        }

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // トップレベルキー（インデント無し・非空）に到達したら services セクション終了。
            if (Regex.IsMatch(line, @"^[a-z]"))
            {
                Flush();
                current = null;
                break;
            }
            var m = header.Match(line);
            if (m.Success)
            {
                Flush();
                current = m.Groups["name"].Value;
                buf = [];
                continue;
            }
            if (current is not null)
                buf.Add(line);
        }
        Flush();
        return result;
    }
}
