using AwesomeAssertions;
using DashboardService.Domain;
using DashboardService.Features.KnowledgeHealth.Report;

namespace DashboardService.Tests.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, planning#494 決定 3, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0377: 観測値の受け口の入力検証を手書きガード節から AbstractValidator へ
// 移した際の**振る舞い同値**を固定する。
[Trait("TestKind", "Unit")]
public class ReportKnowledgeHealthValidatorTests
{
    private readonly ReportKnowledgeHealthValidator _validator = new();

    private static KnowledgeHealthReportRequest Request(string indicator, int? thresholdDays = null)
        => new(indicator, [new KnowledgeHealthObservationRequest("doc-1")], thresholdDays);

    // 陽性対照: 既知の指標 ＋ しきい値なしは通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(Request(KnowledgeHealthIndicators.OrphanDocuments));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: 正のしきい値は通る。境界（1）も通る。
    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    public void ValidThreshold_Passes(int days)
    {
        var result = _validator.Validate(Request(KnowledgeHealthIndicators.OrphanDocuments, days));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1: 未知の指標名は落ち、**移送前と同じ本文**になる。
    // 本文は `KnowledgeHealthIndicators.All` から組み立てる —— 指標を足したときに
    // メッセージだけが古いまま残る形にしない。
    [Fact]
    public void UnknownIndicator_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(Request("orphan-docs"));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
        ReportKnowledgeHealthValidator.IndicatorInvalidMessage.Should()
            .StartWith("indicator must be one of: ")
            .And.Contain(KnowledgeHealthIndicators.All[0]);
    }

    // 陰性 2: 0 以下のしきい値は落ち、**移送前と同じ本文**になる。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveThreshold_FailsWithOriginalMessage(int days)
    {
        var result = _validator.Validate(Request(KnowledgeHealthIndicators.OrphanDocuments, days));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(ReportKnowledgeHealthValidator.ThresholdInvalidMessage);
        ReportKnowledgeHealthValidator.ThresholdInvalidMessage.Should()
            .Be("thresholdDays must be greater than zero");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 複数違反したときにどれが出るかは宣言順で決まる。移送前のガード節は指標を先に見ていた。
    // 順序を入れ替える変更が入ったらここで止まる。
    [Fact]
    public void MultipleViolations_ReportsIndicatorFirst()
    {
        var result = _validator.Validate(Request("orphan-docs", 0));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].ErrorMessage.Should().Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
    }
}
