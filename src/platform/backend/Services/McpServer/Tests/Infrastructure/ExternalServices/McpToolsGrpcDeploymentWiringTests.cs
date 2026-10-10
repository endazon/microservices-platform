using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, NFR-16, ADR-0024 §2, ADR-0029, ADR-0075, IADR-0379 決定 3・5, IADR-0462（2026-09-26 追記 / #1515, #1255 経路 ④-a）:
// **正の配備ファイル**（compose・helm）と本番の `Program.cs` に対して、ツール申告の gRPC 収集の配線が揃っていることを固定する
// （構成情報 API の `IntrospectionGrpcDeploymentWiringTests` と同型）。
//
// ［2026-10-10 / #1517・[[IADR-0533]] 決定 3］REST の収集を撤去し、宛先のキーを `Mcp__Services__*`（値は gRPC の宛先）へ一本化した。
// 旧キー `Mcp__GrpcServices` は配備ファイルに残っていてはならない（残ると McpServer は起動を止める）。
//
// 🔴 **扇形の経路は 4 か所が揃って初めて 1 宛先が届く**: MCP サーバーの宛先（`Mcp__Services__*`）・
// 宛先のポート（helm `grpcPort` / compose `Grpc__Port`）・宛先の h2c リスナ（`AddPlatformGrpcListener`）・
// gRPC 面（`MapMcpToolEndpoints` が張る）。どれか 1 つが欠けると、その宛先は**申告なしとしか見えない**
// （収集は失敗を申告なしへ畳み、公開構成の要求はドリフトの警告に留まる）—— 例外もヘルスの赤も出ない。
[Trait("TestKind", "Unit")]
public class McpToolsGrpcDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
    internal const string AppSettings = "src/platform/backend/Services/McpServer/appsettings.json";
    private const int GrpcPort = 8081;

    // compose は `Key: value`、helm は `- name: Key` の次行 `value: "..."`。どちらの書式でも引く。
    private static Dictionary<string, string> ReadMap(string file, string prefix)
    {
        var text = ReadRepoFile(file);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, $@"(?m)^\s+{prefix}__([a-z0-9-]+):\s*(\S+)\s*$"))
            map[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        foreach (Match m in Regex.Matches(text, $@"(?m)^\s+- name: {prefix}__([a-z0-9-]+)\s*\r?\n\s+value:\s*""?([^""\r\n]+)""?\s*$"))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    // 1. 収集先は helm・compose・appsettings.json の既定の 3 か所とも同じ 3 宛先で、h2c ポートを指す。旧キーは残っていない。
    [Theory]
    [InlineData(Compose)]
    [InlineData(Helm)]
    public void Every_tool_declaration_target_points_at_the_h2c_port_and_the_retired_key_is_gone(string file)
    {
        var targets = ReadMap(file, "Mcp__Services");
        var defaults = AppSettingsTargets();

        targets.Should().NotBeEmpty("対照: 収集先を 1 件も読めていないなら以下は何も検査していない");
        targets.Keys.Should().BeEquivalentTo(defaults.Keys, "配備の宛先と appsettings.json の既定は同じ宛先の集合である");
        foreach (var (service, url) in targets)
            new Uri(url).Port.Should().Be(GrpcPort, $"{service} の宛先は h2c ポートを指す（HTTP/1.1 の :8080 は h2c を話さない）");
        foreach (var (service, url) in defaults)
            new Uri(url).Port.Should().Be(GrpcPort, $"appsettings.json の {service} の既定も h2c ポートを指す");
        ReadRepoFile(file).Should().NotContain("Mcp__GrpcServices__", "旧キーが残ると McpServer は起動を止める");
    }

    private static Dictionary<string, string> AppSettingsTargets()
    {
        using var doc = JsonDocument.Parse(ReadRepoFile(AppSettings));
        return doc.RootElement.GetProperty("Mcp").GetProperty("Services").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    // 2. gRPC の宛先が指すサービスは、helm で grpcPort を宣言している。
    [Fact]
    public void Every_helm_grpc_target_declares_grpc_port()
    {
        var values = ReadRepoFile(Helm);
        var grpc = ReadMap(Helm, "Mcp__Services");
        grpc.Should().NotBeEmpty();
        foreach (var (service, url) in grpc)
        {
            // helm の Deployment / Service 名は `<key>-service`（values.yaml の scaling 節の注記）。
            var host = new Uri(url).Host;
            host.Should().EndWith("-service", $"{service} の宛先は chart の Service 名である");
            var key = host[..^"-service".Length];
            Block(values, $"  {key}:").Should().MatchRegex($@"(?m)^    grpcPort: {GrpcPort}\s*$",
                $"services.{key} が grpcPort を宣言していないと Service に grpc ポートが無く、{service} の申告は届かない");
        }
    }

    // 3. compose の gRPC 宛先は Grpc__Port と expose を持つ。
    [Fact]
    public void Every_compose_grpc_target_declares_grpc_port_and_expose()
    {
        var compose = ReadRepoFile(Compose);
        var grpc = ReadMap(Compose, "Mcp__Services");
        grpc.Should().NotBeEmpty();
        foreach (var (service, url) in grpc)
        {
            var block = Block(compose, $"  {new Uri(url).Host}:");
            block.Should().MatchRegex($@"(?m)^      Grpc__Port: ""{GrpcPort}""\s*$", $"{service} の h2c リスナ");
            block.Should().MatchRegex($@"(?m)^      - ""{GrpcPort}""\s*$", $"{service} の expose");
        }
    }

    // 4. 収集先のサービスは、本番の Program.cs で h2c リスナを立て、申告の gRPC 面を張っている。
    //    申告するサービスは `McpToolDeclarationSource.ServiceName` の定数で見つける（コードからサービス名を引く）。
    [Fact]
    public void Every_tool_declaration_target_starts_the_h2c_listener_and_maps_the_grpc_face()
    {
        var root = RepoRoot();
        var owners = new[] { "src/platform/backend/Services", "src/knowledge/backend/Services" }
            .SelectMany(d => Directory.GetDirectories(Path.Combine(root, d)))
            .Select(d => (Dir: d, Contracts: Path.Combine(d, "Features", "McpTools", "Declare", "McpToolContracts.cs")))
            .Where(x => File.Exists(x.Contracts))
            .Select(x => (x.Dir, Name: Regex.Match(File.ReadAllText(x.Contracts),
                @"public const string ServiceName = ""([a-z0-9-]+)"";").Groups[1].Value))
            .ToList();

        foreach (var service in AppSettingsTargets().Keys)
        {
            var owner = owners.Where(o => o.Name == service).ToList();
            owner.Should().ContainSingle($"'{service}' を申告するサービスはちょうど 1 つ");
            var program = File.ReadAllText(Path.Combine(owner[0].Dir, "Program.cs"));
            program.Should().Contain("builder.AddPlatformGrpcListener();", $"{service} は h2c リスナを立てる");
            program.Should().Contain("app.MapMcpToolEndpoints();", $"{service} は申告の面を張る");
            File.ReadAllText(Path.Combine(owner[0].Dir, "Features", "McpTools", "Declare", "Endpoint.cs"))
                .Should().Contain("app.MapGrpcService<McpToolDeclarationGrpcService>();",
                    $"{service} の申告の口は REST と gRPC を対で張る");
        }
    }

    // `header` の行から、同じ字下げの次の見出しまでを返す（YAML の 1 ブロック）。
    private static string Block(string text, string header)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, header);
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

    internal static string ReadRepoFile(string relative) =>
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
        throw new DirectoryNotFoundException("リポジトリルート（deploy/docker-compose.yml を持つ）を解決できなかった。");
    }
}
