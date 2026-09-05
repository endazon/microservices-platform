using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.Create;
using FluentValidation;

namespace DocumentService.Tests.Features.Documents.Create;

// FR-05, FR-06, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・3・9: 登録の入力検証を手書きガード節から
// `AbstractValidator` へ移した際の**振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答の**鍵**（`errors` の下のプロパティ名）と
// **メッセージ**まで同じであることを見る —— 鍵だけ変わる退行は状態コードでは捕まらないうえ、
// 画面は `errors` の値を平坦化して出すので**機械クライアントだけが壊れる**。
[Trait("TestKind", "Unit")]
public class CreateDocumentValidatorTests
{
    private readonly CreateDocumentValidator _validator = new();

    private static CreateDocumentRequest Request(
        string title = "ok", Dictionary<string, string>? attributes = null)
        => new(title, null, null,
            attributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" },
            null);

    // 陽性対照: 既定集合・属性集合のどちらも満たす要求は通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void ValidRequest_PassesBothRuleSets()
    {
        var req = Request();

        _validator.Validate(req).IsValid.Should().BeTrue();
        _validator.Validate(req, o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet))
            .IsValid.Should().BeTrue();
    }

    // 🔴 鍵とメッセージの両方を、**定数とリテラルの両方**へ当てる。
    // `OverridePropertyName` を消すと鍵が `Title` になってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTitle_FailsWithOriginalKeyAndMessage(string title)
    {
        var result = _validator.Validate(Request(title));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(CreateDocumentValidator.TitleKey);
        CreateDocumentValidator.TitleKey.Should().Be("title");
        result.Errors[0].ErrorMessage.Should().Be(CreateDocumentValidator.TitleRequiredMessage);
        CreateDocumentValidator.TitleRequiredMessage.Should().Be("タイトルは必須です。");
    }

    // 🔴 **位置の固定（P 軸）。** 属性の 3 規則は `RuleSet` に居るので、既定の `Validate(req)` では
    // **走らない** —— 移送前も属性の検査は 413（本文上限）の後ろに居たからである。
    // 属性規則を `RuleSet` の外へ出すとここで止まる（そして 413 が 400 に化ける）。
    [Fact]
    public void DefaultRuleSet_DoesNotRunAttributeRules()
    {
        var req = Request(attributes: new Dictionary<string, string> { ["dept"] = "sales" });

        _validator.Validate(req).IsValid.Should().BeTrue();
        _validator.Validate(req, o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet))
            .IsValid.Should().BeFalse();
    }

    // FR-05, IADR-0047: 機密区分の欠落。鍵は `Domain/DocumentAttributes` が持つ（3 操作で 1 つ）。
    [Fact]
    public void MissingConfidentiality_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string> { ["dept"] = "sales" }),
            o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
        DocumentAttributes.ConfidentialityKey.Should().Be("confidentiality");
        result.Errors[0].ErrorMessage.Should().Be("機密区分（confidentiality）は必須です。");
    }

    // FR-19, ADR-0054: doc_scope の未知値。**欠落は拒否しない**（下の陽性対照と対）。
    [Fact]
    public void UnknownDocScope_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "unknown-scope",
            }),
            o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.DocScopeKey);
        DocumentAttributes.DocScopeKey.Should().Be("doc_scope");
        result.Errors[0].ErrorMessage.Should()
            .Be("文書スコープ（doc_scope）の値 'unknown-scope' は不正です。"
                + "許容値: private-note / organization。");
    }

    // FR-19, [[IADR-0270]] 決定 2: 一般経路での個人資料の作成は拒否する。
    [Fact]
    public void PrivateNoteScope_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["doc_scope"] = "private-note",
            }),
            o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.DocScopeKey);
        result.Errors[0].ErrorMessage.Should().Be(CreateDocumentValidator.PrivateNoteRouteMessage);
        CreateDocumentValidator.PrivateNoteRouteMessage.Should()
            .Be("個人資料（doc_scope=private-note）はこの経路では作成できません。"
                + "/private-notes（SC-19）または Obsidian 同期から作成してください。");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 複数違反したときにどれが出るかは宣言順で決まる。移送前のガード節は
    // 機密区分 → doc_scope の値域 → 個人資料経路 の順であった。
    // 順序を入れ替える／規則を 1 本消す変更が入るとここで止まる。
    [Fact]
    public void MultipleAttributeViolations_ReportsConfidentialityFirst()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string> { ["doc_scope"] = "private-note" }),
            o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet));

        result.IsValid.Should().BeFalse();
        // 機密区分（欠落）＋ 個人資料経路。doc_scope の値域は `private-note` が正準値なので通る。
        result.Errors.Should().HaveCount(2);
        result.Errors[0].PropertyName.Should().Be(DocumentAttributes.ConfidentialityKey);
    }

    // 陽性対照 2: doc_scope の**欠落**は拒否しない（既存文書へ遡及付与しない方針。ADR-0054 §結果）。
    [Fact]
    public void MissingDocScope_Passes()
    {
        var result = _validator.Validate(
            Request(attributes: new Dictionary<string, string> { ["confidentiality"] = "public" }),
            o => o.IncludeRuleSets(CreateDocumentValidator.AttributesRuleSet));

        result.IsValid.Should().BeTrue();
    }
}
