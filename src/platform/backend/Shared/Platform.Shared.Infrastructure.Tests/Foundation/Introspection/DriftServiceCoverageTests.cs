using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018 (#444): ドリフト検出の「検証不能」の 2 原因を分けて固定する。
//
// 🔴 **偽陽性の抑制と、検証が恒久的に欠けていることの見逃しは別物である。**
// 収集対象（Introspection:Services）に登録が無いサービスの段は、収集器が一度も問い合わせないため
// **永久に突合されない**。これを一過性の到達不能と同じ Info で流すと、宣言はあるのに検証だけが
// 静かに欠けた状態が再起動待ちの雑音に紛れる —— FR-15 の「不一致を検出・警告する」が
// そのサービスについてだけ効かない。
//
// 本試験群は 3 分岐（未設定 / 設定済み到達不能 / 到達可能で欠落）を**同じ宣言に対して**固定する。
// 1 つだけ書くと「常に Warning」「常に Info」の壊れた実装でも通るため、対照条件を必ず置く。
public class DriftServiceCoverageTests
{
    private const string StepName = "convert";
    private const string Service = "conversion-service";

    private static PipelineOptions Declaration(bool enabled = true) => new()
    {
        Events = ["RawDocumentFetched", "DocumentNormalized"],
        Steps =
        [
            new PipelineStepOptions
            {
                Name = StepName,
                Service = Service,
                Consumer = "ConversionService.Features.ConversionJobs.RawDocumentFetchedConsumer",
                Input = "RawDocumentFetched",
                Outputs = ["DocumentNormalized"],
                Enabled = enabled,
            },
        ],
    };

    // FR-15 (#444): 真陽性。宣言の担当サービスが収集対象に無い＝恒久的に検証不能なので Warning。
    [Fact]
    public void 収集対象に登録が無いサービスの段は検証不能をWarningで報告する()
    {
        var effective = new EffectiveCollection(
            [], new HashSet<string>(), new HashSet<string>());

        var findings = DriftDetector.Detect(Declaration(), effective);

        var finding = findings.Should().ContainSingle().Which;
        finding.Kind.Should().Be(DriftDetector.Unverifiable);
        finding.Severity.Should().Be(DriftDetector.SeverityWarning,
            "収集対象に無い段は再起動では解消しない構成の誤りであり、一過性の到達不能と混ぜない");
        finding.Target.Should().Be(StepName, "対象は常に段名である（SC-11 の確定値域）");
        finding.Detail.Should().Contain(Service);
    }

    // FR-15, IADR-0029 (#444): 偽陽性の抑制。設定済みだが応答しない＝一過性なので Info のまま。
    // 上の試験の対照条件であり、これが無いと「常に Warning」の実装でも上が通る。
    [Fact]
    public void 収集対象に登録済みだが応答しないサービスの段は従来どおりInfoに留まる()
    {
        var effective = new EffectiveCollection(
            [], new HashSet<string>(), new HashSet<string> { Service });

        var findings = DriftDetector.Detect(Declaration(), effective);

        var finding = findings.Should().ContainSingle().Which;
        finding.Kind.Should().Be(DriftDetector.Unverifiable);
        finding.Severity.Should().Be(DriftDetector.SeverityInfo,
            "一過性の到達不能を Warning へ上げると誤検知抑制（IADR-0029）が壊れる");
    }

    // FR-15 (#444): 到達できたのに段が無いのは適用漏れである（検証不能へ縮退させない）。
    [Fact]
    public void 到達できたサービスに宣言の段が無ければ適用漏れを報告する()
    {
        var effective = new EffectiveCollection(
            [new ServiceIntrospectionDto(Service, [], [], [])],
            new HashSet<string> { Service },
            new HashSet<string>());

        var findings = DriftDetector.Detect(Declaration(), effective);

        var finding = findings.Should().ContainSingle().Which;
        finding.Kind.Should().Be(DriftDetector.MissingApply);
        finding.Severity.Should().Be(DriftDetector.SeverityWarning);
    }

    // FR-15 (#444): 宣言と実効が一致していれば、収集対象の判定が新設されても不一致は出ない。
    [Fact]
    public void 宣言と実効が一致していれば不一致は出ない()
    {
        var effective = new EffectiveCollection(
            [
                new ServiceIntrospectionDto(Service,
                [
                    new StepIntrospectionDto(
                        StepName,
                        "ConversionService.Features.ConversionJobs.RawDocumentFetchedConsumer",
                        "RawDocumentFetched",
                        ["DocumentNormalized"],
                        true),
                ], [], [])
            ],
            new HashSet<string> { Service },
            new HashSet<string>());

        DriftDetector.Detect(Declaration(), effective).Should().BeEmpty();
    }

    // FR-15 (#444): 宣言で無効な段は、担当サービスが収集対象に無くても報告しない
    // （検証の必要が無いため。未設定の警告を無効な段まで広げると恒常的な雑音になる）。
    [Fact]
    public void 宣言で無効な段は収集対象に無くても報告しない()
    {
        var effective = new EffectiveCollection(
            [], new HashSet<string>(), new HashSet<string>());

        DriftDetector.Detect(Declaration(enabled: false), effective).Should().BeEmpty();
    }

    // FR-15, SC-11 (#444): 報告する値は契約の値域（5 分類・2 値）に閉じる。
    // 新しい種別・深刻度を足していないことを、上の全分岐の出力に対して機械的に確かめる。
    [Fact]
    public void 報告する種別と深刻度は契約の値域に閉じる()
    {
        string[] kinds =
        [
            DriftDetector.MissingApply, DriftDetector.UndeclaredSubscription,
            DriftDetector.StaleStage, DriftDetector.BindingMismatch, DriftDetector.Unverifiable,
        ];
        string[] severities = [DriftDetector.SeverityWarning, DriftDetector.SeverityInfo];

        EffectiveCollection[] cases =
        [
            new([], new HashSet<string>(), new HashSet<string>()),
            new([], new HashSet<string>(), new HashSet<string> { Service }),
            new([new ServiceIntrospectionDto(Service, [], [], [])],
                new HashSet<string> { Service }, new HashSet<string>()),
        ];

        foreach (var effective in cases)
        {
            foreach (var finding in DriftDetector.Detect(Declaration(), effective))
            {
                kinds.Should().Contain(finding.Kind);
                severities.Should().Contain(finding.Severity);
            }
        }
    }
}
