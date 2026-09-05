using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.Update;

namespace DocumentService.Tests.Features.Documents.Update;

// FR-05, FR-06, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9: 編集の入力検証の**振る舞い同値**を固定する。
[Trait("TestKind", "Unit")]
public class UpdateDocumentValidatorTests
{
    private readonly UpdateDocumentValidator _validator = new();

    private static UpdateDocumentRequest Request(
        string title = "ok", Dictionary<string, string>? attributes = null)
        => new(title,
            attributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null);

    // 陽性対照。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(Request());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 鍵とメッセージを、定数とリテラルの両方へ当てる。
    // `OverridePropertyName` を消すと鍵が `Title` になってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTitle_FailsWithOriginalKeyAndMessage(string title)
    {
        var result = _validator.Validate(Request(title));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(UpdateDocumentValidator.TitleKey);
        UpdateDocumentValidator.TitleKey.Should().Be("title");
        result.Errors[0].ErrorMessage.Should().Be(UpdateDocumentValidator.TitleRequiredMessage);
        UpdateDocumentValidator.TitleRequiredMessage.Should().Be("タイトルは必須です。");
    }

    // FR-05, IADR-0047: 更新でも機密区分は必須（属性は全置換のため）。
    [Fact]
    public void MissingConfidentiality_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string> { ["dept"] = "sales" }));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        DocumentAttributes.ConfidentialityKey.Should().Be("confidentiality");
        result.Errors[0].ErrorMessage.Should().Be("機密区分（confidentiality）は必須です。");
    }

    // FR-19, ADR-0054: doc_scope の未知値は 400。
    [Fact]
    public void UnknownDocScope_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(Request(attributes: new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "team",
        }));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.DocScopeKey);
        DocumentAttributes.DocScopeKey.Should().Be("doc_scope");
        result.Errors[0].ErrorMessage.Should()
            .Be("文書スコープ（doc_scope）の値 'team' は不正です。許容値: private-note / organization。");
    }

    // 🔴 **宣言順が応答の契約である。** 移送前のガード節は title → confidentiality → doc_scope で、
    // 3 つとも違反していても本文に出るのは `title` の 1 件だけであった。
    // 順序の入れ替え／規則の削除でここが止まる。
    [Fact]
    public void MultipleViolations_ReportsTitleFirst()
    {
        var result = _validator.Validate(Request("  ", new Dictionary<string, string>
        {
            ["doc_scope"] = "team",
        }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors[0].PropertyName.Should().Be(UpdateDocumentValidator.TitleKey);
        result.Errors[1].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        result.Errors[2].PropertyName.Should().Be(DocumentAttributes.DocScopeKey);
    }

    // 陽性対照 2: doc_scope の欠落は拒否しない（遡及付与しない方針）。
    [Fact]
    public void MissingDocScope_Passes()
    {
        _validator.Validate(Request()).IsValid.Should().BeTrue();
    }
}
