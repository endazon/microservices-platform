using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Pipeline;

// FR-14, FR-15, ADR-0018 (#444): **正の宣言（deploy の pipeline.json そのもの）**に対して、
// 宣言が実際に効いていることを外形から確かめる。
//
// 🔴 **宣言が在ることと、宣言が効いていることは別である。** 本リポジトリでは
// 「pipeline.json の enabled:false が段を止められていなかった」本番欠陥が実測されている
// （規約探索が明示登録と独立に段の型を拾っていた）。同型の穴は宣言の下流にも開き得る ——
// 宣言した段の担当サービスが自己申告の収集対象に入っていなければ、宣言は在るのに
// **突合だけが永久に行われない**。本試験群は「宣言 → 収集 → 実効構成」の各段で、
// 宣言の値が実際に結果を変えることを固定する。
public class PipelineDeclarationEffectivenessTests
{
    private const string DeclarationPath =
        "deploy/helm/microservices-platform/files/pipeline.json";

    // FR-14 (#444): 正の宣言が、生の pipeline.json 形式のまま束縛できる（compose がこの形で渡す）。
    [Fact]
    public void 正の宣言は生の形式のまま束縛できる()
    {
        var pipeline = LoadDeclaration();

        pipeline.Version.Should().Be(1);
        pipeline.Steps.Should().NotBeEmpty("宣言が空だと突合の基準を失う（#146 の回帰）");
        pipeline.Events.Should().NotBeEmpty();
        pipeline.Steps.Should().OnlyContain(
            s => !string.IsNullOrEmpty(s.Name)
                 && !string.IsNullOrEmpty(s.Service)
                 && !string.IsNullOrEmpty(s.Consumer)
                 && !string.IsNullOrEmpty(s.Input),
            "起動時照合（fail-fast）は name/service/consumer/input が揃っていることを前提にする");
    }

    // FR-14 (#444): 正の宣言の入出力イベントは events の列挙に閉じる（型名の打ち間違いの検出）。
    [Fact]
    public void 正の宣言の入出力イベントはevents列挙に閉じる()
    {
        var pipeline = LoadDeclaration();
        var known = pipeline.Events.ToHashSet(StringComparer.Ordinal);

        foreach (var step in pipeline.Steps)
        {
            known.Should().Contain(step.Input, $"段 '{step.Name}' の input");
            foreach (var output in step.Outputs)
                known.Should().Contain(output, $"段 '{step.Name}' の outputs");
        }
        foreach (var source in pipeline.Sources)
            known.Should().Contain(source.Event, $"sources '{source.Service}' の event");
    }

    // FR-15 (#444): 🔴 **宣言した段が「集約の対象」になっていることまで見る。**
    // 収集対象（Introspection:Services）に service が無い段は、宣言が在っても永久に突合されない。
    // compose・Helm の両方を見るのは、片方だけ直したときに「手元では検証される・本番では検証されない」
    // という*再現しない形*で現れるためである（宣言のマウント漏れ #146 と同型）。
    [Theory]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("deploy/helm/microservices-platform/values.yaml")]
    public void 宣言の有効な段の担当サービスは自己申告の収集対象に登録されている(string deploymentFile)
    {
        var pipeline = LoadDeclaration();
        var deployment = ReadRepoFile(deploymentFile);

        var services = pipeline.Steps
            .Where(s => s.Enabled)
            .Select(s => s.Service)
            .Distinct(StringComparer.Ordinal);

        foreach (var service in services)
        {
            // compose は `Introspection__Services__x: url`、Helm は `- name: Introspection__Services__x` と
            // 書式が違う。**どちらの書式でも成立する形で引く**（片方の書式で引くと、もう片方は
            // 「設定が在るのに検出できない」ではなく「無いのに緑」になる側へ倒れる）。
            deployment.Should().MatchRegex(
                $@"(?m)Introspection__Services__{Regex.Escape(service)}\s*(:|$)",
                $"#444: 宣言の段を持つ '{service}' が収集対象に無いと、その段は永久に突合されない");
        }
    }

    // FR-14, FR-15 (#444): 🔴 **enabled の値が実効構成の表示にまで届く。**
    // 段の登録を止めるだけでは足りない —— イベント接続（購読者・発行者）から消えて初めて、
    // 構成ビューアが「その段はもう繋がっていない」と示せる。
    [Fact]
    public void 宣言で無効にした段は実効構成のイベント接続から消える()
    {
        var pipeline = LoadDeclaration();
        var target = pipeline.Steps.First(s => s.Enabled && s.Outputs.Count > 0);

        var enabled = Assemble(pipeline, target, stepEnabled: true);
        var disabled = Assemble(pipeline, target, stepEnabled: false);

        Subscribers(enabled, target.Input).Should().Contain(target.Service);
        Subscribers(disabled, target.Input).Should().NotContain(target.Service,
            "無効化した段は購読者として現れてはならない");

        foreach (var output in target.Outputs)
        {
            Publishers(enabled, output).Should().Contain(target.Service);
            Publishers(disabled, output).Should().NotContain(target.Service,
                "無効化した段は発行者としても現れてはならない");
        }
    }

    // FR-15 (#444): 対照条件。上の試験が「常に空」の壊れた実装で通らないことを保証する。
    [Fact]
    public void 宣言で有効な段は実効構成のイベント接続に現れる()
    {
        var pipeline = LoadDeclaration();
        var target = pipeline.Steps.First(s => s.Enabled);

        var effective = Assemble(pipeline, target, stepEnabled: true);

        effective.Pipeline.Should().Contain(s => s.Name == target.Name && s.Enabled);
        Subscribers(effective, target.Input).Should().Contain(target.Service);
    }

    // --- helpers ---------------------------------------------------------

    // 正の pipeline.json を、本番と同じ読み込み経路（Pipeline:ConfigPath → 束縛）で読む。
    // JSON を直接デシリアライズしないのは、**本番が通る経路そのもの**を試験するためである。
    private static PipelineOptions LoadDeclaration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Pipeline:ConfigPath"] = FindRepoFile(DeclarationPath) });
        builder.AddPlatformPipelineConfig();
        return builder.Configuration.GetPlatformPipeline();
    }

    // 対象段だけを指定の有効状態で自己申告し、実効構成を組み立てる（他の段は宣言どおり）。
    private static EffectiveConfigDto Assemble(
        PipelineOptions pipeline, PipelineStepOptions target, bool stepEnabled)
    {
        var services = pipeline.Steps
            .GroupBy(s => s.Service, StringComparer.Ordinal)
            .Select(g => new ServiceIntrospectionDto(
                g.Key,
                g.Select(s => new StepIntrospectionDto(
                    s.Name, s.Consumer, s.Input, s.Outputs,
                    s.Name == target.Name ? stepEnabled : s.Enabled)).ToList(),
                [], []))
            .ToList();

        var collection = new EffectiveCollection(
            services,
            services.Select(s => s.Service).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>());

        return ConfigInspectionService.Assemble(
            pipeline, collection, new ConfigVersionDto(null, null, null));
    }

    private static IReadOnlyList<string> Subscribers(EffectiveConfigDto config, string @event) =>
        config.EventBindings.FirstOrDefault(b => b.Event == @event)?.Subscribers ?? [];

    private static IReadOnlyList<string> Publishers(EffectiveConfigDto config, string @event) =>
        config.EventBindings.FirstOrDefault(b => b.Event == @event)?.Publishers ?? [];

    private static string ReadRepoFile(string relative) => File.ReadAllText(FindRepoFile(relative));

    // 🔴 **解決できなければ止める（fail-closed）。** 読めなかったファイルを空として扱うと、
    // 宣言を検査するはずの試験が**何も検査しないまま緑**になる。
    // knowledge 側の同等ヘルパ（RepoFile）は参照できない —— platform → knowledge の依存は禁止である。
    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"{relative} をリポジトリルートから解決できませんでした（#444: 宣言と配線の突合が行えません）。",
            relative);
    }
}
