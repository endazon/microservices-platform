using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Llm;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Grpc;

// NFR-09, NFR-16, ADR-0029, ADR-0075, 計画 ADR-0089 決定 1, [[IADR-0379]] 決定 3, [[IADR-0533]] 決定 2 (#1255, #1517):
// **正の配備ファイル**（helm の values.yaml・compose）に、east-west gRPC の 1:1 経路の宛先が揃っていることを固定する。
//
// 🔴 **なぜ要るか。** REST の並走を撤去したので、宛先の env が 1 行欠けた配備は**起動は成功したまま**その経路が
// `UNAVAILABLE` へ黙って縮退する（[[IADR-0533]] 決定 2）。認可の経路なら全員が何も見えなくなる。
// 従前この欠落は `scripts/check-bff-downstreams.js` が呼び出し元の登録から引いた宛先で一部を捕まえていたが、
// 本 PR で REST の登録を外したので、その網は無い。**呼び出し元 × キーの表を本クラスの 1 か所に置き、両ファイルを突き合わせる。**
//
// 扇形（構成の自己申告・ツール申告）は `IntrospectionGrpcDeploymentWiringTests` / `McpToolsGrpcDeploymentWiringTests` が持つ。
[Trait("TestKind", "Unit")]
public class EastWestGrpcDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    private const int GrpcPort = 8081;

    // 宛先の env 名 → 宛先のホスト名（helm / compose）。compose の LLM ゲートウェイだけ Service 名が違う。
    private static readonly Dictionary<string, (string Helm, string Compose)> Destinations = new()
    {
        ["Services__AuthorizationServiceGrpc"] = ("authorization-service", "authorization-service"),
        ["Services__LlmGatewayGrpc"] = ("llmgateway-service", "llm-gateway"),
        ["Services__RetrievalServiceGrpc"] = ("retrieval-service", "retrieval-service"),
        ["Services__DocumentServiceGrpc"] = ("document-service", "document-service"),
        ["Services__GraphServiceGrpc"] = ("graph-service", "graph-service"),
        ["Services__NotificationServiceGrpc"] = ("notification-service", "notification-service"),
        ["Services__DashboardServiceGrpc"] = ("dashboard-service", "dashboard-service"),
    };

    // 🔴 **呼び出し元 × キーの表（唯一の置き場）。** 経路の記号は作業仕様書 20261010_issue-1255-1517 の母集合表と同じ。
    // helm は `services.<HelmKey>`、compose は `services: <ComposeKey>:` のブロックを引く。
    public static TheoryData<string, string, string, string> Routes() => new()
    {
        { "B-1 BFF → 認可", "bff", "bff", "Services__AuthorizationServiceGrpc" },
        { "D-1 BFF → 文書（読み取り）", "bff", "bff", "Services__DocumentServiceGrpc" },
        { "R-2 BFF → 検索（属性値）", "bff", "bff", "Services__RetrievalServiceGrpc" },
        { "A-1 AI 分析 → 認可", "aianalysis", "aianalysis-service", "Services__AuthorizationServiceGrpc" },
        { "L-3 AI 分析 → LLM（生成）", "aianalysis", "aianalysis-service", "Services__LlmGatewayGrpc" },
        { "R-1 AI 分析 → 検索", "aianalysis", "aianalysis-service", "Services__RetrievalServiceGrpc" },
        { "A-2 グラフ → 認可", "graph", "graph-service", "Services__AuthorizationServiceGrpc" },
        { "L-4 グラフ → LLM（AI 提案）", "graph", "graph-service", "Services__LlmGatewayGrpc" },
        { "H-1 グラフ → ダッシュボード", "graph", "graph-service", "Services__DashboardServiceGrpc" },
        { "D-2・D-3 グラフ → 文書（タグ）", "graph", "graph-service", "Services__DocumentServiceGrpc" },
        { "A-3 Wiki → 認可", "wiki", "wiki-service", "Services__AuthorizationServiceGrpc" },
        { "A-4 検索 → 認可", "retrieval", "retrieval-service", "Services__AuthorizationServiceGrpc" },
        { "L-2 検索 → LLM（埋め込み・再順位付け）", "retrieval", "retrieval-service", "Services__LlmGatewayGrpc" },
        { "G-1 検索 → グラフ（近傍展開）", "retrieval", "retrieval-service", "Services__GraphServiceGrpc" },
        { "A-5 MCP → 認可", "mcp", "mcp-service", "Services__AuthorizationServiceGrpc" },
        { "A-6 データソース → 認可", "datasource", "datasource-service", "Services__AuthorizationServiceGrpc" },
        { "L-1 取り込み → LLM（埋め込み）", "ingestion", "ingestion-service", "Services__LlmGatewayGrpc" },
        { "L-5 変換 → LLM（図のコード化）", "conversion", "conversion-service", "Services__LlmGatewayGrpc" },
        { "N-1 文書 → 通知", "document", "document-service", "Services__NotificationServiceGrpc" },
        { "文書 → 認可（gRPC だけの既存経路）", "document", "document-service", "Services__AuthorizationServiceGrpc" },
    };

    // 1. helm: `services.<caller>` の extraEnv / extraEnvAppend に、宛先の Service の h2c ポートが入っている。
    [Theory]
    [MemberData(nameof(Routes))]
    public void Helm_wires_the_grpc_destination(string route, string helmKey, string composeKey, string envKey)
    {
        _ = composeKey;
        var values = ReadRepoFile(Helm);
        var block = Block(values, $"  {helmKey}:", after: "services:");
        var url = HelmEnv(block, envKey);

        url.Should().NotBeNull($"{route}: services.{helmKey} に {envKey} が無いと、起動は成功したまま UNAVAILABLE へ縮退する");
        var uri = new Uri(url!);
        uri.Port.Should().Be(GrpcPort, $"{route}: h2c ポートを指す（HTTP/1.1 の :8080 は h2c を話さない）");
        uri.Host.Should().Be(Destinations[envKey].Helm, $"{route}: chart の Service 名");
    }

    // 2. compose: `<caller>` の environment に、宛先の h2c ポートが入っている。
    [Theory]
    [MemberData(nameof(Routes))]
    public void Compose_wires_the_grpc_destination(string route, string helmKey, string composeKey, string envKey)
    {
        _ = helmKey;
        var compose = ReadRepoFile(Compose);
        var block = Block(compose, $"  {composeKey}:", after: "services:");
        var m = System.Text.RegularExpressions.Regex.Match(block, $@"(?m)^\s+{envKey}:\s*""?([^""\s]+)""?\s*$");

        m.Success.Should().BeTrue($"{route}: compose の {composeKey} に {envKey} が無いと、起動は成功したまま UNAVAILABLE へ縮退する");
        var uri = new Uri(m.Groups[1].Value);
        uri.Port.Should().Be(GrpcPort, $"{route}: h2c ポートを指す");
        uri.Host.Should().Be(Destinations[envKey].Compose, $"{route}: compose のサービス名");
    }

    // 3. 表の env 名は、共有の登録関数が読むキーと同じ綴りである（`:` は env で `__` になる）。
    //    knowledge 側の宛先キーは各サービスの試験（gRPC 実装ごとの試験の「宛先が構成されていれば登録する」）が固定する。
    [Fact]
    public void Env_names_match_the_shared_address_keys()
    {
        Destinations.Should().ContainKey(AuthzScopeGrpcClient.AddressKey.Replace(":", "__"));
        Destinations.Should().ContainKey(LlmGatewayGrpcClientExtensions.AddressKey.Replace(":", "__"));
    }

    // 4. 対照: 表の行数と両ファイルの実在の行数が噛み合っている（表が配備の一部しか見ていないことを捕まえる）。
    //    両ファイルの `Services__*Grpc` の (呼び出し元, キー) はすべて表に載っている。
    [Theory]
    [InlineData(Helm)]
    [InlineData(Compose)]
    public void Every_deployed_grpc_destination_is_in_the_table(string file)
    {
        var lines = ReadRepoFile(file).Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "services:");
        start.Should().BeGreaterThanOrEqualTo(0);
        var table = Routes().Select(r => r.Data).Select(d => (Caller: file == Helm ? d.Item2 : d.Item3, Key: d.Item4)).ToHashSet();
        string? caller = null;
        var found = new List<(string, string)>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith('#'))
                break; // 次の最上位キー
            var h = System.Text.RegularExpressions.Regex.Match(l, @"^  ([a-z0-9-]+):\s*$");
            if (h.Success) caller = h.Groups[1].Value;
            var k = System.Text.RegularExpressions.Regex.Match(l, @"^\s+(?:- name: )?(Services__\w+Grpc)\b");
            if (k.Success && caller is not null) found.Add((caller, k.Groups[1].Value));
        }

        found.Should().NotBeEmpty("対照: 1 件も読めていないなら何も検査していない");
        found.Should().OnlyContain(f => table.Contains(f), $"{file} の gRPC 宛先はすべて呼び出し元 × キーの表に載せる（表を 1 か所に保つ）");
    }

    private static string? HelmEnv(string block, string envKey)
    {
        var m = System.Text.RegularExpressions.Regex.Match(block,
            $@"(?m)^\s+- name: {envKey}\s*\r?\n\s+value:\s*""?([^""\r\n]+)""?\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    // `header` の行（`after` の行より後で最初のもの）から、同じ字下げの次の見出しまでを返す。
    private static string Block(string text, string header, string after)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var from = Array.IndexOf(lines, after);
        from.Should().BeGreaterThanOrEqualTo(0, $"'{after}' が見つからない");
        var start = Array.IndexOf(lines, header, from + 1);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{header.Trim()}' のブロックが見つからない");
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

    // 解決できなければ止める（fail-closed）。
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("リポジトリルート（deploy/docker-compose.yml を持つ）を解決できなかった。");
    }
}
