using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.GrantShare;

namespace DocumentService.Tests.Features.Documents.GrantShare;

// FR-20, ADR-0036 D-06, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1・9: 共有の付与の入力検証の**振る舞い同値**。
[Trait("TestKind", "Unit")]
public class GrantDocumentShareValidatorTests
{
    private readonly GrantDocumentShareValidator _validator = new();

    // 陽性対照（2 種別とも）。
    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    public void ValidRequest_Passes(string subjectType)
    {
        var result = _validator.Validate(new CreateShareRequest(subjectType, "alice"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 鍵は `errors` である（この 1 サイトだけ属性名ではない）。
    // `OverridePropertyName` を消すと `SubjectId` になってここで止まる。
    [Theory]
    [InlineData("user", "")]
    [InlineData("user", "   ")]
    [InlineData("team", "alice")]
    [InlineData("", "alice")]
    public void InvalidSubject_FailsWithOriginalKeyAndMessage(string subjectType, string subjectId)
    {
        var result = _validator.Validate(new CreateShareRequest(subjectType, subjectId));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(GrantDocumentShareValidator.ErrorsKey);
        GrantDocumentShareValidator.ErrorsKey.Should().Be("errors");
        result.Errors[0].ErrorMessage.Should().Be(GrantDocumentShareValidator.SubjectInvalidMessage);
        GrantDocumentShareValidator.SubjectInvalidMessage.Should()
            .Be("subjectType は user / group のいずれか、subjectId は非空である必要があります。");
    }

    // 移送前と同じ式から作られていること（語彙を 2 箇所に持たない）。
    [Fact]
    public void Message_IsBuiltFromTheDomainVocabulary()
    {
        GrantDocumentShareValidator.SubjectInvalidMessage.Should()
            .Be($"subjectType は {string.Join(" / ", ShareSubjectType.All)} のいずれか、"
                + "subjectId は非空である必要があります。");
    }

    // 🔴 **述語の粒度（G 軸）。** 移送前は 1 本の `||` で 1 件を返していた。
    // 2 本の `RuleFor` に割ると失敗が 2 件になってここで止まる。
    [Fact]
    public void BothInvalid_ReportsOneFailure()
    {
        var result = _validator.Validate(new CreateShareRequest("team", ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
    }
}
