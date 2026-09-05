using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.UpdateMetadata;

namespace DocumentService.Tests.Features.Documents.UpdateMetadata;

// FR-05, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・4・9: メタデータ更新の入力検証の**振る舞い同値**。
//
// **題名の規則は無い**（この口は属性とタグだけを更新する）。移送前も 2 本のガード節だけであった。
[Trait("TestKind", "Unit")]
public class UpdateMetadataValidatorTests
{
    private readonly UpdateMetadataValidator _validator = new();

    private static UpdateMetadataRequest Request(Dictionary<string, string>? attributes = null)
        => new(attributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" }, null);

    // 陽性対照。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(Request());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void MissingConfidentiality_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(
            Request(new Dictionary<string, string> { ["dept"] = "sales" }));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        DocumentAttributes.ConfidentialityKey.Should().Be("confidentiality");
        result.Errors[0].ErrorMessage.Should().Be("機密区分（confidentiality）は必須です。");
    }

    [Fact]
    public void UnknownConfidentiality_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(
            Request(new Dictionary<string, string> { ["confidentiality"] = "secret" }));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        result.Errors[0].ErrorMessage.Should()
            .Be("機密区分（confidentiality）の値 'secret' は不正です。"
                + "許容値: public / internal / confidential / restricted。");
    }

    // 🔴 宣言順（confidentiality → doc_scope）が契約である。
    [Fact]
    public void MultipleViolations_ReportsConfidentialityFirst()
    {
        var result = _validator.Validate(
            Request(new Dictionary<string, string> { ["doc_scope"] = "team" }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        result.Errors[1].PropertyName.Should().Be(DocumentAttributes.DocScopeKey);
    }

    // 陽性対照 2: 属性そのものが null でも「機密区分が必須」の 1 件だけである（移送前と同じ）。
    [Fact]
    public void NullAttributes_FailsWithConfidentialityOnly()
    {
        var result = _validator.Validate(new UpdateMetadataRequest(null, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
    }
}
