using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Bff.Tests;

// FR-15, ADR-0018: 宣言（pipeline.json）と実効構成の突合（ドリフト検出）の単体テスト。
// 受け入れ基準「適用漏れ・宣言に無い購読のテストケース」を写像する。
public class DriftDetectorTests
{
    private const string Consumer = "Svc.Composable.Steps.CatalogConsumer";

    private static PipelineOptions Declaration(bool enabled = true) => new()
    {
        Steps =
        [
            new PipelineStepOptions
            {
                Name = "catalog", Service = "document-service", Consumer = Consumer,
                Input = "DocumentNormalized", Outputs = ["DocumentUpdated"], Enabled = enabled
            }
        ]
    };

    private static EffectiveCollection Effective(
        IEnumerable<StepIntrospectionDto> steps, string service = "document-service",
        bool reachable = true)
    {
        var report = new ServiceIntrospectionDto(service, steps.ToList(), [], []);
        IReadOnlySet<string> reach = reachable
            ? new HashSet<string> { service } : new HashSet<string>();
        IReadOnlySet<string> unreach = reachable
            ? new HashSet<string>() : new HashSet<string> { service };
        return new EffectiveCollection([report], reach, unreach);
    }

    private static StepIntrospectionDto CatalogStep(bool enabled = true) =>
        new("catalog", Consumer, "DocumentNormalized", ["DocumentUpdated"], enabled);

    [Fact]
    public void Detect_WhenDeclarationMatchesEffective_ReturnsNoDrift()
    {
        var findings = DriftDetector.Detect(Declaration(), Effective([CatalogStep()]));

        findings.Should().BeEmpty();
    }

    // 適用漏れ: 宣言で有効だが、到達可能なサービスの実効に段が無い。
    [Fact]
    public void Detect_WhenDeclaredStepMissingFromReachableService_ReportsMissingApply()
    {
        var findings = DriftDetector.Detect(
            Declaration(), Effective([], reachable: true));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.MissingApply);
    }

    // 誤検知抑制: 担当サービスが到達不能なら適用漏れと断定せず「検証不能」に留める。
    [Fact]
    public void Detect_WhenServiceUnreachable_ReportsUnverifiableNotMissingApply()
    {
        var findings = DriftDetector.Detect(
            Declaration(), Effective([], reachable: false));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.Unverifiable);
    }

    // FR-15, IADR-0029 (#142): ワーカー段（ingest / ingestion-service）でも適用漏れを検出できる。
    // ワーカーが自己申告するようになり到達可能（reachable）となったため、宣言にある ingest 段が
    // 実効に無ければ Unverifiable ではなく MissingApply として検出される。
    // 注: DriftDetector にワーカー固有の分岐は無い（到達可能な段は種別によらず同一経路で判定される）。
    // 本ケースは Detect_WhenDeclaredStepMissingFromReachableService_ReportsMissingApply と同じ経路を
    // ワーカー段のデータで検証し、受け入れ基準「ワーカー段でも MissingApply を検出できる」を明示的に記録する。
    [Fact]
    public void Detect_WhenWorkerStageMissingFromReachableWorker_ReportsMissingApply()
    {
        var declaration = new PipelineOptions
        {
            Steps =
            [
                new PipelineStepOptions
                {
                    Name = "ingest", Service = "ingestion-service",
                    Consumer = "IngestionService.Worker.Features.Ingestion.DocumentUpdatedConsumer",
                    Input = "DocumentUpdated", Outputs = [], Enabled = true
                }
            ]
        };

        var findings = DriftDetector.Detect(
            declaration, Effective([], service: "ingestion-service", reachable: true));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.MissingApply);
    }

    // 宣言に無い購読: 実効で有効な段が宣言に存在しない。
    [Fact]
    public void Detect_WhenEffectiveHasUndeclaredSubscription_ReportsUndeclared()
    {
        var declaration = new PipelineOptions(); // 段の宣言なし
        var stray = new StepIntrospectionDto(
            "ghost", "Svc.Composable.Steps.GhostConsumer", "DocumentUpdated", [], true);

        var findings = DriftDetector.Detect(declaration, Effective([stray], "wiki-service"));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.UndeclaredSubscription);
    }

    // 古い段の残留: 宣言で無効だが実効で稼働している。
    [Fact]
    public void Detect_WhenDisabledStepStillRunning_ReportsStaleStage()
    {
        var findings = DriftDetector.Detect(
            Declaration(enabled: false), Effective([CatalogStep(enabled: true)]));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.StaleStage);
    }

    // バインディング不一致: 段名一致だが購読イベント型が異なる。
    [Fact]
    public void Detect_WhenInputBindingDiffers_ReportsBindingMismatch()
    {
        var wrong = new StepIntrospectionDto(
            "catalog", Consumer, "DocumentDeleted", ["DocumentUpdated"], true);

        var findings = DriftDetector.Detect(Declaration(), Effective([wrong]));

        findings.Should().ContainSingle()
            .Which.Kind.Should().Be(DriftDetector.BindingMismatch);
    }
}
