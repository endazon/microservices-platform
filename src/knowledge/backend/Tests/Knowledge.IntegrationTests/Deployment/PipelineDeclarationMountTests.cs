using System.Text.RegularExpressions;
using FluentAssertions;

namespace Knowledge.IntegrationTests.Deployment;

// FR-15, IADR-0029 (#146): BFF はドリフト検出の突合基準として宣言（pipeline.json）を必要とする。
// この宣言が BFF にマウントされていないと、実効の全購読が UndeclaredSubscription と誤判定される
// （#118 監査。compose・Helm の両方でこのマウントが欠落していた回帰）。
// 本テストは「BFF が宣言を受け取る配線」が compose・Helm から失われていないことを静的に回帰ガードする
// （helm バイナリに依存せず、YAML テキストを直接検査する。NetworkIsolationTests と同方針）。
[Trait("Category", "Deployment")]
public sealed class PipelineDeclarationMountTests
{
    // compose: BFF は pipeline.json を読み取り専用でマウントし、Pipeline__ConfigPath でそれを指す。
    [Fact]
    public void Compose_Bff_MountsPipelineDeclaration()
    {
        var bff = BffBlock(ReadRepoFile(Path.Combine("deploy", "docker-compose.yml")));

        bff.Should().Contain("Pipeline__ConfigPath",
            "#146: BFF は宣言（pipeline.json）の読み込み先を Pipeline__ConfigPath で指定する");
        bff.Should().MatchRegex(@"pipeline\.json:/etc/microservices-platform/pipeline/pipeline\.json",
            "#146: 正の pipeline.json を BFF へ読み取り専用でマウントする");
    }

    // Helm: BFF に pipelineDeclaration=true が設定され、宣言 ConfigMap がマウントされる。
    [Fact]
    public void Helm_Bff_HasPipelineDeclarationFlag()
    {
        var values = ReadRepoFile(Path.Combine("deploy", "helm", "microservices-platform", "values.yaml"));

        BffValuesBlock(values).Should().MatchRegex(@"(?m)^\s*pipelineDeclaration:\s*true",
            "#146: BFF は pipelineDeclaration: true で宣言 ConfigMap をマウントする");
    }

    // Helm: deployment テンプレートのマウント条件が pipelineSteps だけでなく pipelineDeclaration も含む
    // （段ホストでない BFF も宣言を受け取れる）。この条件が pipelineSteps のみへ後退していないことを固定する。
    [Fact]
    public void Helm_PipelineMountGate_IncludesDeclarationFlag()
    {
        var deployment = ReadRepoFile(
            Path.Combine("deploy", "helm", "microservices-platform", "templates", "deployment.yaml"));

        deployment.Should().MatchRegex(@"or\s+\$svc\.pipelineSteps\s+\$svc\.pipelineDeclaration",
            "#146: pipeline-config のマウント条件は pipelineSteps か pipelineDeclaration のいずれかで成立する");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{relative} をリポジトリルートから解決できませんでした。");
    }

    // docker-compose.yml の `services:` 配下 `bff:`（2 スペース）ブロックを抽出する。
    private static string BffBlock(string compose) => TwoSpaceBlock(compose, "services", "bff");

    // values.yaml の `services:` 配下 `bff:`（2 スペース）ブロックを抽出する。
    private static string BffValuesBlock(string values) => TwoSpaceBlock(values, "services", "bff");

    // トップレベル `section:` 配下の 2 スペースインデント `name:` ブロック本文を、次の 2 スペースキーまで返す。
    private static string TwoSpaceBlock(string yaml, string section, string name)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^{section}:\s*$"));
        if (start < 0) return string.Empty;

        var header = new Regex(@"^  [a-z0-9-]+:\s*$");
        var target = new Regex($@"^  {Regex.Escape(name)}:\s*$");
        var buf = new List<string>();
        var inTarget = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^[a-zA-Z]")) break; // 次のトップレベルキーで終端
            if (target.IsMatch(lines[i])) { inTarget = true; continue; }
            if (inTarget && header.IsMatch(lines[i])) break; // 次のサービスで終端
            if (inTarget) buf.Add(lines[i]);
        }
        return string.Join("\n", buf);
    }
}
