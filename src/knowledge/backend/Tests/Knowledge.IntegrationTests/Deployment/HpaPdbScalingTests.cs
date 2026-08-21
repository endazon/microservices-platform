using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Deployment;

// NFR, IADR-0050 (#197): HPA/PDB による水平スケール・可用性の回帰ガード。
// helm バイナリに依存せず、Helm チャートの YAML テキストを直接検査する（PipelineDeclarationMountTests と同方針）。
// 次の変更で「HPA/PDB 対象リストの縮退」「HPA 対象 Deployment への静的 replicas 復活」「テンプレート消失」が
// 起きても CI（このテスト）で検知できるようにする（PR 本文の手動 helm template ログだけでは回帰を守れない）。
[Trait("Category", "Deployment")]
public sealed class HpaPdbScalingTests
{
    // IADR-0050: HPA/PDB 適用対象＝ステートレス要求処理系 10 サービス（ワーカーは対象外）。
    private static readonly string[] ExpectedScalingServices =
    [
        "bff", "retrieval", "authorization", "aianalysis", "document",
        "datasource", "dashboard", "feedback", "wiki", "llmgateway",
    ];

    // ワーカー（キュー駆動）は CPU-HPA が不適のため対象外（IADR-0050）。回帰で混入していないこと。
    private static readonly string[] ExcludedWorkers = ["conversion", "ingestion"];

    [Fact]
    public void Values_Scaling_EnabledWithExpectedServices()
    {
        var scaling = ScalingBlock(ReadChartFile("values.yaml"));

        scaling.Should().MatchRegex(@"(?m)^\s*enabled:\s*true",
            "#197: scaling.enabled=true で HPA/PDB を有効化する");

        foreach (var svc in ExpectedScalingServices)
            scaling.Should().MatchRegex($@"(?m)^\s*-\s*{Regex.Escape(svc)}\s*$",
                $"#197/IADR-0050: {svc} は HPA/PDB 対象（scaling.services）に含まれる");

        foreach (var worker in ExcludedWorkers)
            Regex.IsMatch(scaling, $@"(?m)^\s*-\s*{Regex.Escape(worker)}\s*$").Should().BeFalse(
                $"IADR-0050: ワーカー {worker} は CPU-HPA 対象外（scaling.services に含めない）");

        // claude-review #213: 想定外の対象（11 件目）が紛れ込んでいないよう、対象リストの総数も固定する。
        // scaling ブロック内の唯一のリストは services のため、`- <name>` 行数＝対象サービス数。
        var listItemCount = Regex.Matches(scaling, @"(?m)^\s*-\s*[a-z]").Count;
        listItemCount.Should().Be(ExpectedScalingServices.Length,
            $"#197/IADR-0050: HPA/PDB 対象は正確に {ExpectedScalingServices.Length} 件（想定外の追加を検知する）");

        scaling.Should().MatchRegex(@"minReplicas:\s*2", "#197: 要求処理系は minReplicas>=2");
        scaling.Should().MatchRegex(@"minAvailable:\s*1", "#197: PDB は minAvailable>=1");
    }

    [Fact]
    public void HpaTemplate_ExistsAndDrivenByScalingServices()
    {
        var hpa = ReadChartFile(Path.Combine("templates", "hpa.yaml"));

        hpa.Should().Contain("kind: HorizontalPodAutoscaler");
        hpa.Should().Contain("autoscaling/v2", "HPA は autoscaling/v2 を使う");
        hpa.Should().MatchRegex(@"has\s+\$name\s+\$\.Values\.scaling\.services",
            "#197: HPA は scaling.services リスト駆動で生成される");
    }

    [Fact]
    public void PdbTemplate_ExistsAndDrivenByScalingServices()
    {
        var pdb = ReadChartFile(Path.Combine("templates", "pdb.yaml"));

        pdb.Should().Contain("kind: PodDisruptionBudget");
        pdb.Should().Contain("policy/v1", "PDB は policy/v1 を使う");
        pdb.Should().MatchRegex(@"has\s+\$name\s+\$\.Values\.scaling\.services",
            "#197: PDB は scaling.services リスト駆動で生成される");
    }

    // HPA 対象は Deployment に静的 replicas を出力しない（HPA が所有）。この抑止条件が壊れると
    // HPA と Deployment の replicas が綱引きするため、ガード条件をテキストで固定する。
    [Fact]
    public void Deployment_SuppressesStaticReplicas_ForScalingServices()
    {
        var deployment = ReadChartFile(Path.Combine("templates", "deployment.yaml"));

        deployment.Should().MatchRegex(
            @"if\s+not\s+\(and\s+\$\.Values\.scaling\.enabled\s+\(has\s+\$name\s+\$\.Values\.scaling\.services\)\)",
            "#197/IADR-0050: HPA 対象サービスは Deployment の静的 replicas を出力しない");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadChartFile(string relative)
    {
        var rel = Path.Combine("deploy", "helm", "microservices-platform", relative);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{rel} をリポジトリルートから解決できませんでした。");
    }

    // values.yaml のトップレベル `scaling:` ブロック本文を、次のトップレベルキーまで返す。
    private static string ScalingBlock(string values)
    {
        var lines = values.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^scaling:\s*$"));
        if (start < 0) return string.Empty;

        var buf = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^[a-zA-Z]")) break; // 次のトップレベルキーで終端
            buf.Add(lines[i]);
        }
        return string.Join("\n", buf);
    }
}
