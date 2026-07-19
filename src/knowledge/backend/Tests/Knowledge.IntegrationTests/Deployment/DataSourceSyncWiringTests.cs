using System.Text.RegularExpressions;
using FluentAssertions;

namespace Knowledge.IntegrationTests.Deployment;

// FR-01, UC-04, NFR(15分以内反映), IADR-0051, IADR-0074 (#299): データソース定期同期の本番デプロイ配線を
// 静的に回帰ガードする。本 issue の障害は「ワーカーは実装済みだが Helm values に DataSourceSync 設定が無く
// 実環境で定期同期が回らない」ことだった。helm バイナリに依存せず YAML/テンプレートのテキストを直接検査する
// （HpaPdbScalingTests / PipelineDeclarationMountTests と同方針）。PR 本文の手動 helm template ログだけでは
// 回帰（値の消失・env 名の破損・経路B 無効化）を CI で守れないため固定する。
[Trait("Category", "Deployment")]
public sealed class DataSourceSyncWiringTests
{
    // 本番 values.yaml: datasource サービスで定期同期が有効・間隔 300 秒（NFR 15 分反映の根拠値）。
    [Fact]
    public void ProdValues_Datasource_EnablesPeriodicSync()
    {
        var block = ServiceBlock(ReadChartFile("values.yaml"), "datasource");

        block.Should().MatchRegex(@"(?m)^\s*dataSourceSync:\s*$",
            "#299: datasource に dataSourceSync ブロックが存在する");
        block.Should().MatchRegex(@"(?m)^\s*enabled:\s*true",
            "#299: 本番は定期同期を有効化する（既定無効ワーカーの config 有効化）");
        block.Should().MatchRegex(@"(?m)^\s*intervalSeconds:\s*300",
            "#299/IADR-0074: 本番間隔は 300 秒（検出 ≤5 分 + 下流予算 ≥10 分 < NFR 15 分）");
    }

    // deployment テンプレート: dataSourceSync を持つサービスにのみ env を描画する（後方互換）。
    // env 名は ASP.NET の __→: 規約で DataSourceSyncOptions（SectionName=DataSourceSync）へバインドする。
    [Fact]
    public void DeploymentTemplate_RendersSyncEnv_GatedOnDataSourceSync()
    {
        var deployment = ReadChartFile(Path.Combine("templates", "deployment.yaml"));

        deployment.Should().MatchRegex(@"if\s+\$svc\.dataSourceSync",
            "#299: env は dataSourceSync を持つサービスにのみ描画する（未設定サービスは従来どおり）");
        deployment.Should().Contain("DataSourceSync__Enabled",
            "#299: DataSourceSyncOptions.Enabled への env（__→: 規約）を描画する");
        deployment.Should().Contain("DataSourceSync__IntervalSeconds",
            "#299: DataSourceSyncOptions.IntervalSeconds への env を描画する");
    }

    // 経路B（ローカル k8s）: values-local.yaml で明示有効化＋間隔 60 秒（監査検証環境。本番像は不変）。
    [Fact]
    public void LocalValues_Datasource_EnablesSync_WithShortInterval()
    {
        var block = ServiceBlock(ReadRepoFile(Path.Combine("deploy", "local", "values-local.yaml")), "datasource");

        block.Should().MatchRegex(@"(?m)^\s*dataSourceSync:\s*$",
            "#299: 経路B でも datasource.dataSourceSync を明示する");
        block.Should().MatchRegex(@"(?m)^\s*enabled:\s*true",
            "#299/IADR-0074: 経路B は監査検証のため定期同期を有効化する");
        block.Should().MatchRegex(@"(?m)^\s*intervalSeconds:\s*60",
            "#299/IADR-0074: 経路B は反映確認を高速化するため間隔 60 秒（本番 300 秒は不変）");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadChartFile(string relative) =>
        ReadRepoFile(Path.Combine("deploy", "helm", "microservices-platform", relative));

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

    // `services:` 配下の 2 スペースインデントのサービス（`  <name>:`）本文を、次の同段キーまで返す。
    private static string ServiceBlock(string values, string name)
    {
        var lines = values.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^  {Regex.Escape(name)}:\s*$"));
        if (start < 0) return string.Empty;

        var buf = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            // 次の同段キー（`  <key>:`）またはトップレベルキーで終端する。
            if (Regex.IsMatch(lines[i], @"^  [a-zA-Z]") || Regex.IsMatch(lines[i], @"^[a-zA-Z]"))
                break;
            buf.Add(lines[i]);
        }
        return string.Join("\n", buf);
    }
}
