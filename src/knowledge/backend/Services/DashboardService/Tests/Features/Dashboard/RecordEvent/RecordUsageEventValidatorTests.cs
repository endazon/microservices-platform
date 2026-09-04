using AwesomeAssertions;
using DashboardService.Features.Dashboard.RecordEvent;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Tests.Features.Dashboard.RecordEvent;

// FR-10, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0376:
// 利用イベント記録の入力検証を手書きガード節から AbstractValidator へ移した際の
// **振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class RecordUsageEventValidatorTests
{
    private readonly RecordUsageEventValidator _validator = new();

    // 陽性対照: 既知の種別は通る。**大小の揺れは不正ではない**（正規化は端点側の Normalize が行う）。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Theory]
    [InlineData("search")]
    [InlineData("answer")]
    [InlineData("ANSWER")]
    [InlineData("Search")]
    public void ValidEventType_Passes(string eventType)
    {
        var result = _validator.Validate(new UsageEventRequest(eventType, "経費規程"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陰性: 未知・空・null は落ち、**移送前と同じ本文**になる。
    [Theory]
    [InlineData("click")]
    [InlineData("")]
    [InlineData(" search")]
    [InlineData(null)]
    public void InvalidEventType_FailsWithOriginalMessage(string? eventType)
    {
        var result = _validator.Validate(new UsageEventRequest(eventType!));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(RecordUsageEventValidator.EventTypeInvalidMessage);
        RecordUsageEventValidator.EventTypeInvalidMessage.Should()
            .Be("eventType must be 'search' or 'answer'");
    }
}
