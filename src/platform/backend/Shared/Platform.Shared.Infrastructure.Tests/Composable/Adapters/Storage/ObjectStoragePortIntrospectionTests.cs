using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Composable.Adapters.Storage;

// FR-15, ADR-0018, ADR-0014 / ADR-0106, IADR-0029 フォローアップ 2 (#2118): 実効構成 API の「ポート実装の選択（…ストレージ…）」
// （計画 10_composability §5）。オブジェクトストレージを使うサービスは、選択中の実装と接続先を自己申告に載せる。
//
// 🔴 **申告と実体が同じ判定で決まる**こと（AddPlatformObjectStorage の IsConfigured）と、**資格情報を載せない**ことを見る。
//    自己申告は構成情報 API（管理者向け）を通って画面と監査へ出る。
public class ObjectStoragePortIntrospectionTests
{
    private static IConfiguration ConfigOf(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"ObjectStorage:{p.Key}", p => p.Value))
            .Build();

    private static PortSelectionDto PortOf(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddPlatformIntrospection("svc", new PipelineOptions(), i => i.AddObjectStoragePort(config));
        var report = services.BuildServiceProvider().GetRequiredService<ServiceIntrospectionDto>();
        return report.Ports.Should().ContainSingle(p => p.Port == ObjectStorageExtensions.IntrospectionPort).Which;
    }

    [Fact]
    public void 構成済みなら実クライアントとエンドポイントのホストとポートだけを申告する()
    {
        var port = PortOf(ConfigOf(
            ("Endpoint", "http://ak:sk@seaweedfs:8333/knowledge?token=abc"), ("AccessKey", "ak-secret"), ("SecretKey", "sk-secret")));

        port.Implementation.Should().Be(nameof(S3ObjectStorageClient));
        port.Target.Should().Be("seaweedfs:8333");
        (port.Target ?? "").Should().NotContain("sk").And.NotContain("token");
    }

    // 陽性対照の逆側: 未構成なら縮退クライアントを申告する（「常に S3」と申告する実装と区別する）。
    [Theory]
    [InlineData("http://seaweedfs:8333", null)]
    [InlineData(null, "sk")]
    public void 未構成なら縮退クライアントを接続先なしで申告する(string? endpoint, string? secret)
    {
        var port = PortOf(ConfigOf(("Endpoint", endpoint), ("AccessKey", "ak"), ("SecretKey", secret)));

        port.Implementation.Should().Be(nameof(NullObjectStorageClient));
        port.Target.Should().BeNull();
    }

    [Theory]
    [InlineData("http://wiki-js:3000/graphql", "wiki-js:3000")]
    [InlineData("https://user:pass@example.internal/graphql?apikey=x", "example.internal:443")]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 接続先はホストとポートだけで読めない値は申告しない(string? url, string? expected)
    {
        IntrospectionTarget.HostAndPort(url).Should().Be(expected);
    }

    // 🔴 構造の試験: **オブジェクトストレージを使うサービスは、必ずそのポートを申告する。** 列挙を持たず、
    //    `AddPlatformObjectStorage(` を呼ぶ Program.cs を走査で見つける（足したサービスが黙って申告から漏れない）。
    [Fact]
    public void オブジェクトストレージを使うサービスはすべてそのポートを申告する()
    {
        var root = RepoRoot();
        var programs = new[] { "src/platform/backend/Services", "src/knowledge/backend/Services" }
            .SelectMany(d => Directory.GetDirectories(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar))))
            .Select(d => Path.Combine(d, "Program.cs"))
            .Where(File.Exists)
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(p => p.Text.Contains("AddPlatformObjectStorage(builder.Configuration)", StringComparison.Ordinal))
            .ToList();

        programs.Should().HaveCountGreaterThanOrEqualTo(6, "対照: 2026-10-26 時点で 6 サービスが使っている（走査が壊れて 0 件になったときに緑を返さない）");
        foreach (var (path, text) in programs)
        {
            Regex.IsMatch(text, @"AddPlatformIntrospection\([\s\S]*?\.AddObjectStoragePort\(builder\.Configuration\)")
                .Should().BeTrue($"{Path.GetRelativePath(root, path)} はオブジェクトストレージを使うのに object-storage のポートを申告していない");
        }
    }

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
