using AuthorizationService.Api.Domain;
using AuthorizationService.Api.Services;
using FluentAssertions;

namespace AuthorizationService.Api.Tests;

// FR-09, UC-05, ADR-0004: 属性辞書・ポリシー・文書属性バリデーションの単体テスト
public class AbacValidationTests
{
    private static AttributeDefinition Confidentiality() =>
        AttributeDefinition.Create("confidentiality", "機密区分",
            ["public", "internal", "confidential", "restricted"], required: true, AttributeScope.Document);

    private static AttributeDefinition Clearance() =>
        AttributeDefinition.Create("clearance", "取扱区分",
            ["internal", "confidential", "restricted"], required: false, AttributeScope.User);

    // ---- 属性辞書 ----

    // FR-09: 正常な属性辞書はエラー無し
    [Fact]
    public void ValidateAttributeDefinition_Valid_NoErrors()
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            "department", "部門", ["hr", "eng"], AttributeScope.Document, []);
        errors.Should().BeEmpty();
    }

    // FR-09: key 未指定はエラー
    [Fact]
    public void ValidateAttributeDefinition_MissingKey_Error()
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            "", "部門", ["hr"], AttributeScope.Document, []);
        errors.Should().Contain(e => e.Contains("key"));
    }

    // FR-09: 許可値が空はエラー
    [Fact]
    public void ValidateAttributeDefinition_EmptyAllowedValues_Error()
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            "department", "部門", [], AttributeScope.Document, []);
        errors.Should().Contain(e => e.Contains("allowedValues"));
    }

    // FR-09: 許可値の重複はエラー
    [Fact]
    public void ValidateAttributeDefinition_DuplicateAllowedValues_Error()
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            "department", "部門", ["hr", "HR"], AttributeScope.Document, []);
        errors.Should().Contain(e => e.Contains("重複"));
    }

    // FR-09: 不正なスコープはエラー
    [Fact]
    public void ValidateAttributeDefinition_InvalidScope_Error()
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            "department", "部門", ["hr"], "galaxy", []);
        errors.Should().Contain(e => e.Contains("scope"));
    }

    // FR-09: 同一スコープでのキー重複はエラー
    [Fact]
    public void ValidateAttributeDefinition_DuplicateKeyInScope_Error()
    {
        var existing = new[] { Confidentiality() };
        var errors = AbacValidation.ValidateAttributeDefinition(
            "confidentiality", "別ラベル", ["public"], AttributeScope.Document, existing);
        errors.Should().Contain(e => e.Contains("既に定義済み"));
    }

    // FR-09: 別スコープなら同名キーを許容
    [Fact]
    public void ValidateAttributeDefinition_SameKeyDifferentScope_NoError()
    {
        var existing = new[] { Confidentiality() }; // document スコープ
        var errors = AbacValidation.ValidateAttributeDefinition(
            "confidentiality", "利用者側", ["internal"], AttributeScope.User, existing);
        errors.Should().BeEmpty();
    }

    // FR-09: 更新時は自分自身を一意チェックから除外する
    [Fact]
    public void ValidateAttributeDefinition_UpdateSelf_NoError()
    {
        var self = Confidentiality();
        var errors = AbacValidation.ValidateAttributeDefinition(
            self.Key, "更新後ラベル", ["public", "internal"], self.Scope,
            new[] { self }, excludeId: self.Id);
        errors.Should().BeEmpty();
    }

    // ---- ポリシー ----

    // FR-09: 定義済みキーの許可値に整合するポリシーはエラー無し
    [Fact]
    public void ValidatePolicy_Valid_NoErrors()
    {
        var defs = new[] { Confidentiality(), Clearance() };
        var errors = AbacValidation.ValidatePolicy(
            "eng-read", PolicyAction.Read,
            new() { ["clearance"] = ["confidential"] },
            new() { ["confidentiality"] = ["public", "internal"] },
            defs);
        errors.Should().BeEmpty();
    }

    // FR-09: 不正なアクションはエラー
    [Fact]
    public void ValidatePolicy_InvalidAction_Error()
    {
        var errors = AbacValidation.ValidatePolicy(
            "p", "delete", new(), new(), []);
        errors.Should().Contain(e => e.Contains("action"));
    }

    // FR-09, UC-05: 辞書外の文書属性値を条件に含むポリシーはエラー（矛盾検証）
    [Fact]
    public void ValidatePolicy_DocValueOutsideDictionary_Error()
    {
        var defs = new[] { Confidentiality() };
        var errors = AbacValidation.ValidatePolicy(
            "p", PolicyAction.Read,
            new(),
            new() { ["confidentiality"] = ["top-secret"] },
            defs);
        errors.Should().Contain(e => e.Contains("辞書外"));
    }

    // FR-09: 未定義キーの条件は許容（段階導入）
    [Fact]
    public void ValidatePolicy_UndefinedKey_Allowed()
    {
        var errors = AbacValidation.ValidatePolicy(
            "p", PolicyAction.Read,
            new(),
            new() { ["project"] = ["apollo"] },
            []);
        errors.Should().BeEmpty();
    }

    // FR-09: 条件の値集合が空はエラー
    [Fact]
    public void ValidatePolicy_EmptyConditionValues_Error()
    {
        var errors = AbacValidation.ValidatePolicy(
            "p", PolicyAction.Read,
            new(),
            new() { ["confidentiality"] = [] },
            new[] { Confidentiality() });
        errors.Should().Contain(e => e.Contains("空にできません"));
    }

    // FR-09: 条件を省略（null）してもドメインは空辞書として保存し、null を保持しない（NRE 回帰防止）
    [Fact]
    public void AbacPolicy_Create_NullConditions_StoredAsEmpty()
    {
        var policy = AbacPolicy.Create("p", PolicyAction.Read, null, null);
        policy.UserConditions.Should().NotBeNull().And.BeEmpty();
        policy.DocumentConditions.Should().NotBeNull().And.BeEmpty();
    }

    // FR-09, IADR-0006: ポリシーの参照判定（scope 一致のキーのみ参照とみなす）
    [Fact]
    public void PolicyReferencesAttribute_MatchesByScopeAndKey()
    {
        var policy = AbacPolicy.Create("p", PolicyAction.Read,
            new() { ["clearance"] = ["confidential"] },
            new() { ["confidentiality"] = ["public"] });

        AbacValidation.PolicyReferencesAttribute(policy, "confidentiality", AttributeScope.Document)
            .Should().BeTrue();
        AbacValidation.PolicyReferencesAttribute(policy, "clearance", AttributeScope.User)
            .Should().BeTrue();
        // scope 不一致（同名キーでも別スコープ）は参照とみなさない
        AbacValidation.PolicyReferencesAttribute(policy, "confidentiality", AttributeScope.User)
            .Should().BeFalse();
        AbacValidation.PolicyReferencesAttribute(policy, "unused", AttributeScope.Document)
            .Should().BeFalse();
    }

    // ---- 文書属性 ----

    // FR-09: 必須属性を満たし許可値内なら valid
    [Fact]
    public void ValidateDocumentAttributes_Valid_NoErrors()
    {
        var defs = new[] { Confidentiality() };
        var errors = AbacValidation.ValidateDocumentAttributes(
            new() { ["confidentiality"] = "internal" }, defs);
        errors.Should().BeEmpty();
    }

    // FR-09: 必須属性の欠落はエラー
    [Fact]
    public void ValidateDocumentAttributes_MissingRequired_Error()
    {
        var defs = new[] { Confidentiality() };
        var errors = AbacValidation.ValidateDocumentAttributes(
            new() { ["department"] = "eng" }, defs);
        errors.Should().Contain(e => e.Contains("必須属性"));
    }

    // FR-09: 許可値外の属性値はエラー
    [Fact]
    public void ValidateDocumentAttributes_ValueOutsideAllowed_Error()
    {
        var defs = new[] { Confidentiality() };
        var errors = AbacValidation.ValidateDocumentAttributes(
            new() { ["confidentiality"] = "top-secret" }, defs);
        errors.Should().Contain(e => e.Contains("許可値に含まれません"));
    }

    // FR-09: 未定義キー（自由タグ）は許容
    [Fact]
    public void ValidateDocumentAttributes_UndefinedKey_Allowed()
    {
        var defs = new[] { Confidentiality() };
        var errors = AbacValidation.ValidateDocumentAttributes(
            new() { ["confidentiality"] = "public", ["topic"] = "onboarding" }, defs);
        errors.Should().BeEmpty();
    }
}
