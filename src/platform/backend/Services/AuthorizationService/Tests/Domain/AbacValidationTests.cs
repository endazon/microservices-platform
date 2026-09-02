using AuthorizationService.Domain;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

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

    // FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）: write は有効な値域である
    // （上の否定形と対の陽性対照。値域を広げた側が固定されないと、否定形だけでは
    // 「常に action エラーを返す実装」も緑になる）。
    [Fact]
    public void ValidatePolicy_WriteAction_IsValid()
    {
        var errors = AbacValidation.ValidatePolicy(
            "owner-write", PolicyAction.Write, new(), new(), []);
        errors.Should().BeEmpty();
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

    // ---- 文書条件のキー数（planning#470 の裁定・暫定統制） ----

    // FR-05, FR-09, SC-09: **文書条件に 2 つ以上の属性キーを持つポリシーは保存できない。**
    //
    // 🔴 認可スコープは選言を運べないため、評価器は複数ポリシーの文書条件を**キー単位 union**で
    // 1 本の連言へ潰す。多キーポリシーが複数マッチすると
    // **どのポリシー単独も許可しない値の混成**が通る（planning#470 の反例）。
    [Fact]
    public void ValidatePolicy_MultiKeyDocumentConditions_Error()
    {
        var errors = AbacValidation.ValidatePolicy(
            "多キー", "read",
            new Dictionary<string, List<string>> { ["clearance"] = ["restricted"] },
            new Dictionary<string, List<string>>
            {
                ["confidentiality"] = ["public"],
                ["department"] = ["sales"],
            },
            [Confidentiality(), Clearance()]);

        errors.Should().Contain(e => e.Contains("documentConditions"));
    }

    // 🔴 陽性対照 1: **1 キーは通る。** これが無いと「文書条件を持つポリシーを一律拒否」でも
    // 上の否定形が緑になる。
    [Fact]
    public void ValidatePolicy_SingleKeyDocumentConditions_NoErrors()
    {
        var errors = AbacValidation.ValidatePolicy(
            "単キー", "read",
            new Dictionary<string, List<string>> { ["clearance"] = ["restricted"] },
            new Dictionary<string, List<string>> { ["confidentiality"] = ["public", "internal"] },
            [Confidentiality(), Clearance()]);

        errors.Should().BeEmpty();
    }

    // 🔴 陽性対照 2: **利用者条件は何キーあっても通る。**
    // 潰しているのは文書条件の側だけであり、制限を利用者条件へ広げてはならない。
    [Fact]
    public void ValidatePolicy_MultiKeyUserConditions_NoErrors()
    {
        var errors = AbacValidation.ValidatePolicy(
            "利用者条件は多キーでよい", "read",
            new Dictionary<string, List<string>>
            {
                ["clearance"] = ["restricted"],
                ["department"] = ["sales"],
            },
            new Dictionary<string, List<string>> { ["confidentiality"] = ["public"] },
            [Confidentiality(), Clearance()]);

        errors.Should().BeEmpty();
    }

    // 陽性対照 3: 文書条件が空のポリシー（既存テストが作る形）は従来どおり通る。
    [Fact]
    public void ValidatePolicy_EmptyDocumentConditions_NoErrors()
    {
        var errors = AbacValidation.ValidatePolicy(
            "文書条件なし", "read",
            new Dictionary<string, List<string>> { ["clearance"] = ["restricted"] },
            new Dictionary<string, List<string>>(),
            [Confidentiality(), Clearance()]);

        errors.Should().BeEmpty();
    }
}
