using AwesomeAssertions;
using NotificationService.Features.Notifications.Accept;

namespace NotificationService.Tests.Features.Notifications.Accept;

// FR-22, UC-11, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0215 決定 2・4 /
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・7・9: 受け口の検証器の単体試験（#1278 PR-D）。
//
// 🔴 **鍵もメッセージも「定数」と「リテラル」の両方へ当てる。** 片方だけを見ると、定数の値を
// 書き換えた変更が試験ごと追随して緑のまま通る（[[IADR-0398]] 決定 9）。
//
// 🔴 **本サイトは形 β である。** 端点契約試験（`NotificationIngressValidationProblemContractTests`）が
// HTTP 応答の鍵の列を見るのに対し、本ファイルは `ValidationResult` の側で
// **宣言順・件数・粒度**を見る。
public class NotificationIngressValidatorTests
{
    private static readonly DateTimeOffset Occurred = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly NotificationIngressValidator Validator = new();

    private static NotificationIngressRequest Request(
        string? subject = "alice",
        string? kind = "private-note.purge.weekly",
        DateTimeOffset? occurredAt = null,
        int? count = 3,
        int? thresholdPercent = null,
        DateTimeOffset? deadline = null)
        => new(subject, kind, occurredAt ?? Occurred, count, thresholdPercent, deadline);

    // 陽性対照 —— **満たす要求は通る。** これが無いと「常に落ちる検証器」でも他の試験が緑になる。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = Validator.Validate(Request());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 鍵の明示（`OverridePropertyName`）。推論名は `Subject`（PascalCase）であり、
    // 移送前の `subject` と一致しない。**定数とリテラルの両方**へ当てる。
    [Fact]
    public void MissingSubject_FailsWithOriginalKeyAndMessage()
    {
        var failure = Validator.Validate(Request(subject: "   ")).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.SubjectKey);
        failure.PropertyName.Should().Be("subject");
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.SubjectRequiredMessage);
        failure.ErrorMessage.Should().Be("subject は必須である。");
    }

    [Fact]
    public void TooLongSubject_FailsWithOriginalKeyAndMessage()
    {
        var subject = new string('a', NotificationIngress.SubjectMaxLength + 1);
        var failure = Validator.Validate(Request(subject: subject)).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.SubjectKey);
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.SubjectTooLongMessage);
        failure.ErrorMessage.Should().Be("subject は 255 文字以内である。");
    }

    [Fact]
    public void MissingKind_FailsWithOriginalKeyAndMessage()
    {
        var failure = Validator.Validate(Request(kind: "")).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.KindKey);
        failure.PropertyName.Should().Be("kind");
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.KindRequiredMessage);
        failure.ErrorMessage.Should().Be("kind は必須である。");
    }

    [Fact]
    public void TooLongKind_FailsWithOriginalKeyAndMessage()
    {
        var kind = new string('k', NotificationIngress.KindMaxLength + 1);
        var failure = Validator.Validate(Request(kind: kind)).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.KindKey);
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.KindTooLongMessage);
        failure.ErrorMessage.Should().Be("kind は 100 文字以内である。");
    }

    [Fact]
    public void MissingOccurredAt_FailsWithOriginalKeyAndMessage()
    {
        var request = new NotificationIngressRequest("alice", "x", null, 3, null, null);
        var failure = Validator.Validate(request).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.OccurredAtKey);
        failure.PropertyName.Should().Be("occurredAt");
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.OccurredAtRequiredMessage);
        failure.ErrorMessage.Should().Be("occurredAt は必須である。");
    }

    [Fact]
    public void NegativeCount_FailsWithOriginalKeyAndMessage()
    {
        var failure = Validator.Validate(Request(count: -1)).Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.CountKey);
        failure.PropertyName.Should().Be("count");
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.CountNegativeMessage);
        failure.ErrorMessage.Should().Be("count は 0 以上である。");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ThresholdPercentOutOfRange_FailsWithOriginalKeyAndMessage(int percent)
    {
        var failure = Validator.Validate(Request(thresholdPercent: percent))
            .Errors.Should().ContainSingle().Subject;

        failure.PropertyName.Should().Be(NotificationIngressValidator.ThresholdPercentKey);
        failure.PropertyName.Should().Be("thresholdPercent");
        failure.ErrorMessage.Should().Be(NotificationIngressValidator.ThresholdPercentOutOfRangeMessage);
        failure.ErrorMessage.Should().Be("thresholdPercent は 0〜100 である。");
    }

    // 🔴 **形 β の契約そのもの**: 5 項目が同時に不正なら **5 件**が**宣言順**で並び、
    // `ToDictionary()` の鍵も同じ順序で 5 つ・各 1 件になる。
    // 規則を 1 本消すと件数が減り、宣言順を入れ替えると並びが変わる。
    [Fact]
    public void AllFiveInvalid_ReportsFiveFailuresInDeclarationOrder()
    {
        var request = new NotificationIngressRequest("   ", "", null, -1, 101, null);

        var result = Validator.Validate(request);

        result.Errors.Select(e => e.PropertyName).Should().Equal(
            ["subject", "kind", "occurredAt", "count", "thresholdPercent"]);
        result.ToDictionary().Keys.Should().Equal(
            ["subject", "kind", "occurredAt", "count", "thresholdPercent"]);
        result.ToDictionary().Values.Should().AllSatisfy(v => v.Should().ContainSingle());
    }

    // 🔴 **述語の粒度**（[[IADR-0398]] 決定 7 / IADR-0395 決定 8）。空白 300 文字は上限 255 を
    // 超えるが、移送前は `IsNullOrWhiteSpace` が真になった時点で `else if` へ**到達しない**ので
    // **1 件（必須）**である。規則レベルの `Cascade(CascadeMode.Stop)` を外すと 2 件になる。
    [Fact]
    public void WhitespaceSubjectOverTheLimit_ReportsRequiredOnly()
    {
        var result = Validator.Validate(Request(subject: new string(' ', 300)));

        result.Errors.Should().ContainSingle();
        result.Errors[0].ErrorMessage.Should().Be(NotificationIngressValidator.SubjectRequiredMessage);
    }

    [Fact]
    public void WhitespaceKindOverTheLimit_ReportsRequiredOnly()
    {
        var result = Validator.Validate(Request(kind: new string(' ', 300)));

        result.Errors.Should().ContainSingle();
        result.Errors[0].ErrorMessage.Should().Be(NotificationIngressValidator.KindRequiredMessage);
    }

    // 🔴 **述語を広げていない。** null の `count` / `thresholdPercent` は移送前も正当である
    // （`is < 0` / `is < 0 or > 100` は null に対して偽）。`NotNull()` を足すとここで赤になる。
    [Fact]
    public void NullCountAndThreshold_Pass()
    {
        Validator.Validate(Request(count: null, thresholdPercent: null)).IsValid.Should().BeTrue();
    }

    // **過去の期限も正当である**（IADR-0215 決定 4）。`deadline` に規則を置くとここで赤になる。
    [Fact]
    public void PastDeadline_Passes()
    {
        Validator.Validate(Request(deadline: Occurred.AddYears(-5))).IsValid.Should().BeTrue();
    }

    // **種別の値集合は開いている**（IADR-0215 決定 2）。値集合を閉じるとここで赤になる。
    [Fact]
    public void UnknownKind_Passes()
    {
        Validator.Validate(Request(kind: "some-future-kind")).IsValid.Should().BeTrue();
    }

    // 境界 —— 0 / 100 / 上限ちょうどの長さは通る。
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ThresholdPercentAtTheBoundary_Passes(int percent)
    {
        Validator.Validate(Request(thresholdPercent: percent)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SubjectAndKindAtTheExactLimit_Pass()
    {
        var request = Request(
            subject: new string('a', NotificationIngress.SubjectMaxLength),
            kind: new string('k', NotificationIngress.KindMaxLength));

        Validator.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ZeroCount_Passes()
    {
        Validator.Validate(Request(count: 0)).IsValid.Should().BeTrue();
    }

    // 🔴 **長さのメッセージは DB 列長の定数から作られている。** 数値を直書きすると、
    // 列長を変えたときにメッセージだけが古いまま残る。
    [Fact]
    public void LengthMessages_AreBuiltFromTheColumnLimits()
    {
        NotificationIngressValidator.SubjectTooLongMessage.Should()
            .Be($"subject は {NotificationIngress.SubjectMaxLength} 文字以内である。");
        NotificationIngressValidator.KindTooLongMessage.Should()
            .Be($"kind は {NotificationIngress.KindMaxLength} 文字以内である。");
    }
}
