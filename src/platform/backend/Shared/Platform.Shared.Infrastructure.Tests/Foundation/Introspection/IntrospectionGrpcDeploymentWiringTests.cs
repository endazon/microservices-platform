using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3・5, IADR-0462 (#1514, #1255 経路 ⑤):
// **正の配備ファイル**（compose・helm）と本番の `Program.cs` に対して、自己申告の gRPC 収集の配線が揃っていることを固定する。
//
// 🔴 **扇形の経路は 4 か所が揃って初めて 1 宛先が移る**: BFF の gRPC 宛先（`Introspection__GrpcServices__*`）・
// 宛先のポート（helm `grpcPort` / compose `Grpc__Port`）・宛先の h2c リスナ（`AddPlatformGrpcListener`）・
// gRPC 面（`MapPlatformIntrospection` が張る）。どれか 1 つが欠けると、その宛先は**到達不能としか見えない**
// （収集器は失敗を到達不能へ隔離する。ドリフト検出は Info に留める）—— 例外もヘルスの赤も出ない。
public class IntrospectionGrpcDeploymentWiringTests
{
    private const string Compose = "deploy/docker-compose.yml";
    private const string Helm = "deploy/helm/microservices-platform/values.yaml";
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

    // 1. REST の収集先すべてに gRPC の宛先があり、同じ DNS 名の h2c ポートを指す。
    [Theory]
    [InlineData(Compose)]
    [InlineData(Helm)]
    public void Every_rest_introspection_target_has_a_grpc_target_on_the_same_host(string file)
    {
        var rest = ReadMap(file, "Introspection__Services");
        var grpc = ReadMap(file, "Introspection__GrpcServices");

        rest.Should().NotBeEmpty("対照: 収集先を 1 件も読めていないなら以下は何も検査していない");
        grpc.Keys.Should().BeEquivalentTo(rest.Keys, "REST の収集先と gRPC の収集先は同じ集合である（片方だけの宛先を作らない）");

        foreach (var (service, restUrl) in rest)
        {
            var g = new Uri(grpc[service]);
            g.Host.Should().Be(new Uri(restUrl).Host, $"{service} の gRPC 宛先は REST と同じ Service を指す");
            g.Port.Should().Be(GrpcPort, $"{service} の gRPC 宛先は h2c ポートを指す");
        }
    }

    // 2. gRPC の宛先が指すサービスは、helm で grpcPort を・compose で Grpc__Port と expose を宣言している。
    [Fact]
    public void Every_helm_grpc_target_declares_grpc_port()
    {
        var values = ReadRepoFile(Helm);
        foreach (var (service, url) in ReadMap(Helm, "Introspection__GrpcServices"))
        {
            // helm の Deployment / Service 名は `<key>-service`（values.yaml の scaling 節の注記）。
            var host = new Uri(url).Host;
            host.Should().EndWith("-service", $"{service} の宛先は chart の Service 名である");
            var key = host[..^"-service".Length];
            Block(values, $"  {key}:").Should().MatchRegex($@"(?m)^    grpcPort: {GrpcPort}\s*$",
                $"services.{key} が grpcPort を宣言していないと Service に grpc ポートが無く、{service} は到達不能になる");
        }
    }

    [Fact]
    public void Every_compose_grpc_target_declares_grpc_port_and_expose()
    {
        var compose = ReadRepoFile(Compose);
        foreach (var (service, url) in ReadMap(Compose, "Introspection__GrpcServices"))
        {
            var block = Block(compose, $"  {new Uri(url).Host}:");
            block.Should().MatchRegex($@"(?m)^      Grpc__Port: ""{GrpcPort}""\s*$", $"{service} の h2c リスナ");
            block.Should().MatchRegex($@"(?m)^      - ""{GrpcPort}""\s*$", $"{service} の expose");
        }
    }

    // 3. 収集先のサービスは、本番の Program.cs で h2c リスナを立て、自己申告の面を張っている。
    [Fact]
    public void Every_introspection_target_program_starts_the_h2c_listener_and_maps_introspection()
    {
        var root = RepoRoot();
        var programs = new[] { "src/platform/backend/Services", "src/knowledge/backend/Services" }
            .SelectMany(d => Directory.GetDirectories(Path.Combine(root, d)))
            .Select(d => Path.Combine(d, "Program.cs"))
            .Where(File.Exists)
            .ToDictionary(p => p, File.ReadAllText);

        foreach (var service in ReadMap(Helm, "Introspection__Services").Keys)
        {
            var owner = programs.Where(kv => kv.Value.Contains($"AddPlatformIntrospection(\"{service}\"", StringComparison.Ordinal))
                .Select(kv => kv).ToList();
            owner.Should().ContainSingle($"'{service}' を申告する Program.cs はちょうど 1 つ");
            var source = owner[0].Value;
            source.Should().Contain("builder.AddPlatformGrpcListener();", $"{service} は h2c リスナを立てる");
            source.Should().Contain("app.MapPlatformIntrospection();", $"{service} は自己申告の面（REST と gRPC）を張る");
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
        throw new DirectoryNotFoundException("リポジトリルート（deploy/docker-compose.yml を持つ）を解決できなかった。");
    }
}
