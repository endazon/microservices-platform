using AwesomeAssertions;
using DotNet.Testcontainers.Images;
using Knowledge.IntegrationTests.Fixtures;
using Xunit;

namespace Knowledge.IntegrationTests.Storage;

// FR-06, ADR-0106 決定 4・5, ADR-0107 決定 3・4, [[IADR-0461]] (#1499):
// 受け入れ試験（ObjectStorageRoundTripTests）が**配備と同じ SeaweedFS** を試していることの確認。
//
// 🔴 **Trait を付けない**（コンテナを起こさないので PR の ci.yml で走る）。
// 受け入れ試験そのものは Integration（develop への push・日次）でしか走らない。PR の段で
// 「試験が別のイメージ・別の起動形を試している」「テレメトリの無効化が抜けた」を止めるのが本クラスの役目である。
public sealed class SeaweedFsContainerDefinitionTests
{
    private const string Digest = "sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882";

    // ADR-0107 決定 3: タグだけの参照は、同じタグの中身が差し替わっても検知できない。
    [Fact]
    public void Test_image_is_pinned_by_digest()
    {
        var image = new DockerImage(SeaweedFsContainer.Image);

        image.Repository.Should().Be("chrislusf/seaweedfs");
        image.Tag.Should().Be("4.47");
        image.Digest.Should().Be(Digest);
    }

    // ADR-0106 決定 5 / 08_data-egress-policy: SeaweedFS のテレメトリは既定で有効である。
    [Fact]
    public void Test_container_disables_telemetry()
    {
        SeaweedFsContainer.ServerArguments.Should().Contain("-master.telemetry=false");
    }

    // [[IADR-0461]] 決定 2・10: 引数は**全体を**固定する。1 つ足したり外したりしても（例: gRPC の口を既定へ戻す、
    // 内部の口を 0.0.0.0 へ開く）ここで止まる。部分一致の検査は、黙って口が増える変更を見逃す。
    [Fact]
    public void Test_container_arguments_are_pinned_in_full()
    {
        SeaweedFsContainer.ServerArguments.Should().Equal(
            "server",
            "-ip=127.0.0.1",
            "-ip.bind=127.0.0.1",
            "-s3",
            "-s3.ip.bind=0.0.0.0",
            "-s3.port=8333",
            "-s3.port.grpc=18333",
            "-s3.port.iceberg=0",
            "-s3.port.lance=0",
            "-master.telemetry=false");
    }

    // [[IADR-0461]] 決定 10: S3 ゲートウェイの gRPC の管理用 RPC は、署名鍵が空だと認証なしで通る。
    // 起動スクリプトが毎回乱数の鍵を与え、そのうえで entrypoint へ引数をそのまま渡すこと。
    [Fact]
    public void Test_container_sets_a_random_filer_signing_key_before_starting()
    {
        SeaweedFsContainer.StartupScript.Should()
            .StartWith("export WEED_JWT_FILER_SIGNING_KEY=\"$(head -c 32 /dev/urandom")
            .And.EndWith("exec /entrypoint.sh \"$@\"");
    }

    // 試験が確かめた形と配備の形がずれると、受け入れ試験は配備の保証にならない。
    [Fact]
    public void Compose_uses_the_same_image_script_and_arguments()
    {
        var compose = File.ReadAllText(RepoFile("deploy/docker-compose.yml"));
        var service = Section(compose.Replace("\r\n", "\n"), "\n  seaweedfs:\n", "\n  ", "compose の seaweedfs サービス");

        compose.Should().Contain($"image: {SeaweedFsContainer.Image}");
        // compose は `$` を変数展開するので、ファイル上は `$$` で書く。
        ListUnder(service, "    entrypoint:", "      - ").Select(Unquote).Select(v => v.Replace("$$", "$"))
            .Should().Equal("/bin/sh", "-c", SeaweedFsContainer.StartupScript, "seaweedfs");
        ListUnder(service, "    command:", "      - ").Should().Equal(SeaweedFsContainer.ServerArguments);
    }

    [Fact]
    public void Helm_uses_the_same_image_script_and_arguments()
    {
        var values = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/values.yaml"));
        var template = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/templates/seaweedfs.yaml"));

        values.Should().Contain("image: chrislusf/seaweedfs").And.Contain("tag: \"4.47\"")
            .And.Contain($"digest: \"{Digest}\"");
        Section(values, "\nseaweedfs:", "\n# ", "seaweedfs:").Should().Contain("\n  port: 8333\n");
        ListUnder(template, "          command:", "            - ").Select(Unquote)
            .Should().Equal("/bin/sh", "-c", SeaweedFsContainer.StartupScript, "seaweedfs");
        // `-s3.port` は values の port から描く（上で 8333 を確かめた）。
        ListUnder(template, "          args:", "            - ").Select(a => a.Replace("{{ $s.port }}", "8333"))
            .Should().Equal(SeaweedFsContainer.ServerArguments);
    }

    // [[IADR-0461]] 決定 10: NetworkPolicy が有効な配備では、SeaweedFS の Pod へ入れるのは S3 HTTP（8333）だけ。
    // 名前空間全体の許可（allow-intra-namespace）が SeaweedFS を選ぶと、許可の和で gRPC まで開いてしまう。
    [Fact]
    public void Helm_network_policy_admits_only_the_s3_port_into_seaweedfs()
    {
        var template = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/templates/seaweedfs.yaml"));
        var intra = File.ReadAllText(RepoFile("deploy/helm/microservices-platform/templates/networkpolicy.yaml"));

        var policy = template[template.IndexOf("name: allow-seaweedfs-s3-only", StringComparison.Ordinal)..];
        policy.Should().Contain("app: seaweedfs").And.Contain("port: {{ $s.port }}").And.NotContain("18333");
        var allowIntra = intra[intra.IndexOf("name: allow-intra-namespace", StringComparison.Ordinal)..];
        allowIntra[..allowIntra.IndexOf("policyTypes:", StringComparison.Ordinal)]
            .Should().Contain("operator: NotIn").And.Contain("values: [seaweedfs]");
    }

    // text の中で start から始まる節を、次に end が現れるまで切り出す（start が無ければ失敗させる）。
    private static string Section(string text, string start, string end, string label)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, $"{label} の節が見つからない");
        var body = from + start.Length;
        var next = text.IndexOf(end, body, StringComparison.Ordinal);
        while (next >= 0 && next + end.Length < text.Length && text[next + end.Length] == ' ')
            next = text.IndexOf(end, next + 1, StringComparison.Ordinal);
        return next < 0 ? text[from..] : text[from..next];
    }

    // header 行の直後から、itemPrefix で始まる行を連続して集める（YAML のブロック列）。
    private static List<string> ListUnder(string text, string header, string itemPrefix)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, l => l == header);
        at.Should().BeGreaterThanOrEqualTo(0, $"`{header.Trim()}` が見つからない");
        var items = new List<string>();
        for (var i = at + 1; i < lines.Length && lines[i].StartsWith(itemPrefix, StringComparison.Ordinal); i++)
            items.Add(lines[i][itemPrefix.Length..]);
        items.Should().NotBeEmpty($"`{header.Trim()}` の下に要素が無い");
        return items;
    }

    // YAML の単一引用符スカラーを外す（`''` は `'` 1 つ）。引用符の無い値はそのまま返す。
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'")
            : value;

    // リポジトリの実ファイルをテストから引く（出力ディレクトリから上へ辿る）。
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"リポジトリのファイルが見つからない: {relative}");
    }
}
