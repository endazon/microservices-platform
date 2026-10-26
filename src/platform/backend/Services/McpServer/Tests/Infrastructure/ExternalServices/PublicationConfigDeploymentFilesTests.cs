using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, ADR-0024（公開構成は Git 管理・CI スキーマ検証・GitOps 適用）, IADR-0645 (#2117):
// **配備ファイルに書いた MCP の公開構成を、起動時と同じ読み込み（ToolPublicationConfigLoader ＝ 検証器
// ToolPublicationConfigValidator）に CI で通す。**
//
// 🔴 規則を別の言語・別の関数へ写さない。検証器の注記どおり、2 か所に書くと片方だけが緩む。
//    そのため公開構成は Helm values に**JSON の文字列のまま**（`mcpPublication: |` のブロック）書く決まりにし
//    （IADR-0645 決定 2）、ここでブロックを取り出して**読み込みそのもの**へ渡す。
//    起動時の fail-fast（ToolPublicationFailFastTests）は配備の後でしか効かない。本試験はそれを PR の時点へ前倒しする。
[Trait("TestKind", "Unit")]
public class PublicationConfigDeploymentFilesTests : IDisposable
{
    private const string CiValues = "deploy/helm/microservices-platform/ci/mcp-publication-values.yaml";
    private const string ImageDefault = "src/platform/backend/Services/McpServer/Configuration/mcp-publication.json";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcp-pub-deploy-{Guid.NewGuid():N}");

    public PublicationConfigDeploymentFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- 取り出し ------------------------------------------------------------------------------

    private static readonly Regex KeyLine = new(@"^(?<indent>[ ]*)mcpPublication:(?<rest>.*)$", RegexOptions.Compiled);

    // 1 つの YAML の文字列から公開構成を取り出す。`mcpPublication: |` のブロックだけを受け付け、
    // **空（`""`）は「差し替えない」**として飛ばす。それ以外の書き方（1 行の JSON・`>`・YAML の木）は
    // 取り出せないので**誤りとして返す** —— 黙って飛ばすと、その構成は CI の検証を素通りして配備される。
    internal static (IReadOnlyList<(int Line, string Json)> Blocks, IReadOnlyList<string> Errors) Extract(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<(int, string)>();
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = KeyLine.Match(lines[i]);
            if (!m.Success) continue;
            var rest = Regex.Replace(m.Groups["rest"].Value, @"\s+#.*$", "").Trim();
            if (rest is "\"\"" or "''") continue;
            if (rest != "|")
            {
                errors.Add($"{i + 1} 行目: mcpPublication は `|` のブロック（JSON の文字列）で書く（IADR-0645）。書き方: '{rest}'");
                continue;
            }
            var keyIndent = m.Groups["indent"].Value.Length;
            var body = new List<string>();
            int? indent = null;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var l = lines[j];
                if (l.Trim().Length == 0) { body.Add(""); continue; }
                var ind = l.Length - l.TrimStart(' ').Length;
                if (ind <= keyIndent) break;
                indent ??= ind;
                if (ind < indent) break;
                body.Add(l[indent.Value..]);
            }
            blocks.Add((i + 1, string.Join('\n', body).TrimEnd() + "\n"));
        }
        return (blocks, errors);
    }

    // 取り出した JSON を**起動時と同じ読み込み**に通し、違反の文言を返す（空なら合格）。
    private IReadOnlyList<string> Validate(string json)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            LoaderFor(path).Load();
            return [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return [ex.Message];
        }
    }

    private IReadOnlyList<string> ValidateYaml(string text)
    {
        var (blocks, errors) = Extract(text);
        return [.. errors, .. blocks.SelectMany(b => Validate(b.Json).Select(e => $"{b.Line} 行目のブロック: {e}"))];
    }

    private static ToolPublicationConfigLoader LoaderFor(string path)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ToolPublicationConfigLoader.PathKey] = path })
            .Build());

    // ---- 本体: リポジトリの配備ファイル --------------------------------------------------------

    // 🔴 FR-16, ADR-0024（CI スキーマ検証）: deploy/ 以下の全 YAML の公開構成が、起動時と同じ検証を通る。
    // **列挙を持たない**（走査で見つける）。**0 件の走査を緑にしない**（CI 用の values に 1 つ置いてある）。
    [Fact]
    public void 配備ファイルに書いた公開構成はすべて起動時と同じ検証を通る()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))
            .ToList();
        files.Should().NotBeEmpty("対照: deploy/ の YAML を 1 件も読めていないなら以下は何も検査していない");

        var found = 0;
        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var (blocks, _) = Extract(text);
            found += blocks.Count;
            violations.AddRange(ValidateYaml(text).Select(e => $"{Path.GetRelativePath(root, file)}: {e}"));
        }

        found.Should().BeGreaterThan(0, "走査が壊れて 0 件になったときに緑を返さない（CI 用の values に 1 つ在るはず）");
        violations.Should().BeEmpty("配備ファイルの公開構成は起動時の検証に通らなければ適用できない（ADR-0024 §5）");
    }

    // FR-16: イメージに焼き込んだ既定（values が空のときに読まれる）も同じ検証を通る。
    [Fact]
    public void イメージの既定の公開構成も起動時と同じ検証を通る()
    {
        Validate(File.ReadAllText(Path.Combine(RepoRoot(), ImageDefault))).Should().BeEmpty();
    }

    // 🔴 FR-16, IADR-0645 (#2117): **ConfigMap の中身で起動する。** CI 用の values のブロック（ConfigMap の値そのもの。
    // scripts/helm-mcp-publication.test.js が「ConfigMap の値は values の文字列とバイト等価」を固定している）を
    // Mcp:PublicationConfigPath で指すと、その中身のツールが読まれる。イメージの既定とは中身が違う（取り違えると赤）。
    [Fact]
    public void CI用のvaluesの公開構成をMcp_PublicationConfigPathで指すとその中身で起動する()
    {
        var (blocks, errors) = Extract(File.ReadAllText(Path.Combine(RepoRoot(), CiValues)));
        errors.Should().BeEmpty();
        blocks.Should().ContainSingle();
        var path = Path.Combine(_dir, "mcp-publication.json");
        File.WriteAllText(path, blocks[0].Json);

        var config = LoaderFor(path).Load();

        config.Version.Should().Be("ci-2026-10-26");
        config.Tools.Select(t => t.PublishedName ?? t.Name)
            .Should().BeEquivalentTo(["search_documents", "document.get_document"]);
        config.ServiceAccountAttributes.Should().ContainKey("batch-agent");

        // 対照: イメージの既定とは中身が違う（同じだと、差し替えが効かずに既定を読んでいても上が通る）。
        var image = LoaderFor(Path.Combine(RepoRoot(), ImageDefault)).Load();
        image.Version.Should().NotBe(config.Version);
    }

    // ---- 取り出しと検証の単体（不正な公開構成が CI で落ちること） ------------------------------

    private static string Values(string json) =>
        "services:\n  mcp:\n    mcpPublication: |\n"
        + string.Join('\n', json.Split('\n').Select(l => "      " + l)) + "\n    resources: {}\n";

    // FR-16 (#2117・陽性対照): 正しい構成は通る。これが無いと「何でも落とす取り出し」と区別できない。
    [Fact]
    public void 正しい公開構成のブロックは通る()
    {
        ValidateYaml(Values("""{"version":"v1","tools":[{"name":"retrieval.search","service":"retrieval"}]}"""))
            .Should().BeEmpty();
    }

    // FR-16, ADR-0024 §5: 検証器が弾くものは、配備ファイルに書いても CI で落ちる（検証器の規則をそのまま使っている証拠）。
    [Theory]
    [InlineData("""{"version":"v1","tools":[{"name":"ai.summarize","service":"ai-analysis"}]}""", "初期公開範囲外")]
    [InlineData("""{"version":"v1","tools":[{"name":"graph.get_cluster_summary","service":"graph"}]}""", "初期公開範囲外")]
    [InlineData("""{"version":"v1","tools":[{"name":"a.x","service":"a","published_name":"x"},{"name":"b.x","service":"b","published_name":"x"}]}""", "重複")]
    [InlineData("""{"version":"v1","tools":[],"service_account_attributes":{"batch-agent":{"doc_scope":"private-note"}}}""", "private-note")]
    [InlineData("""{"version":"v1","tools":[],"service_account_attributes":{"batch-agent":{"projects":"ai-stock-trading"}}}""", "ai-stock-trading")]
    [InlineData("""{"version":"","tools":[]}""", "version")]
    public void 検証器が弾く公開構成は配備ファイルに書くとCIで落ちる(string json, string reason)
    {
        ValidateYaml(Values(json)).Should().ContainSingle().Which.Should().Contain(reason);
    }

    // FR-16 (#2117): JSON として壊れた構成も落ちる（起動時は JsonException で止まる）。
    [Fact]
    public void JSONとして壊れた公開構成は落ちる()
    {
        ValidateYaml(Values("""{ "version": "v1", tools: [""")).Should().ContainSingle();
    }

    // 🔴 FR-16 (#2117): ブロック以外の書き方は**取り出せない**ので、黙って飛ばさず誤りにする
    // （飛ばすと、その構成は CI の検証を素通りして配備される）。空は「差し替えない」として通す。
    [Theory]
    [InlineData("    mcpPublication: '{\"version\":\"v1\",\"tools\":[]}'")]
    [InlineData("    mcpPublication: >")]
    [InlineData("    mcpPublication:")]
    public void ブロック以外の書き方は誤りになる(string line)
    {
        ValidateYaml($"services:\n  mcp:\n{line}\n      x: 1\n").Should().ContainSingle()
            .Which.Should().Contain("`|` のブロック");
    }

    [Fact]
    public void 空の値は差し替えなしとして通る()
    {
        var (blocks, errors) = Extract("services:\n  mcp:\n    mcpPublication: \"\"  # 既定\n    resources: {}\n");
        blocks.Should().BeEmpty();
        errors.Should().BeEmpty();
    }

    // FR-16 (#2117): ブロックは次の同じ字下げのキーの手前で終わる（後続のキーを JSON に混ぜない）。
    [Fact]
    public void ブロックは次のキーの手前で終わる()
    {
        var (blocks, _) = Extract(Values("""{"version":"v1","tools":[]}"""));
        blocks.Should().ContainSingle().Which.Json.Should().Be("{\"version\":\"v1\",\"tools\":[]}\n");
    }

    // 解決できなければ止める（fail-closed）。McpToolsGrpcDeploymentWiringTests と同じ判定。
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
