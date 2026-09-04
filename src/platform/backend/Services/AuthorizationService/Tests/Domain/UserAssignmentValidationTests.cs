using AuthorizationService.Domain;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

// FR-05, FR-09, UC-05, SC-17, ADR-0026 (#452): 利用者への割当（ロール・ABAC 属性）の検証。
//
// 🔴 **判定は HTTP を通さずに試験する。** 画面テストも端点テストも「値集合から 1 値落としても
// 落ちない」ことが実測されている（IADR-0129 決定 6）。値域と必須の判定そのものは、ここで固定する。
[Trait("TestKind", "Unit")]
public class UserAssignmentValidationTests
{
    private static List<AttributeDefinition> Dictionary() =>
    [
        AttributeDefinition.Create("department", "所属部門",
            ["engineering", "sales", "hr", "finance"], false, AttributeScope.User),
        AttributeDefinition.Create("clearance", "取扱可能区分",
            ["public", "internal", "confidential", "restricted"], false, AttributeScope.User),
        // 任意属性（計画の「タグ」に当たる）。**必須にしない。**
        AttributeDefinition.Create("tags", "タグ",
            ["finance", "management"], false, AttributeScope.User),
        // 文書スコープの同名キー。**利用者への割当には使えない**（スコープを跨がない）。
        AttributeDefinition.Create("confidentiality", "機密区分",
            ["public", "internal"], true, AttributeScope.Document),
    ];

    // ---- ロール割当（必須・複数選択・定義済みロールのみ・併任可） ----

    // 05_screens §SC-17: ロール割当は**必須**である。
    [Fact]
    public void ValidateRoles_rejects_an_empty_assignment()
    {
        UserAssignmentValidation.ValidateRoles([], ["platform-admin"])
            .Should().ContainSingle().Which.Should().Contain("roles は必須");
        UserAssignmentValidation.ValidateRoles(null, ["platform-admin"])
            .Should().ContainSingle();
    }

    // 05_screens §SC-17: **定義済みロールのみ**。値域は IdP が持ち、ここへ焼き込まない。
    [Fact]
    public void ValidateRoles_rejects_roles_outside_the_assignable_set()
    {
        var errors = UserAssignmentValidation.ValidateRoles(
            ["platform-admin", "realm-management"], ["platform-admin", "platform-operator"]);
        errors.Should().ContainSingle().Which.Should().Contain("realm-management");
    }

    // 05_screens §SC-17: **併任可**（複数選択）。陽性対照 —— 上の否定形だけだと
    // 「常に拒否する」実装でも緑になる。
    [Fact]
    public void ValidateRoles_accepts_multiple_assignable_roles()
    {
        UserAssignmentValidation.ValidateRoles(
            ["platform-admin", "platform-operator"], ["platform-admin", "platform-operator"])
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateRoles_rejects_duplicates_and_blanks()
    {
        UserAssignmentValidation.ValidateRoles(
            ["platform-admin", "platform-admin"], ["platform-admin"])
            .Should().ContainSingle().Which.Should().Contain("重複");
        UserAssignmentValidation.ValidateRoles(["  "], ["platform-admin"])
            .Should().NotBeEmpty();
    }

    // ---- ABAC 属性割当（部門・機密区分上限は必須／タグは任意／定義済みの値のみ） ----

    // 05_screens §SC-17: 部門・機密区分上限は**必須**。
    [Theory]
    [InlineData("department")]
    [InlineData("clearance")]
    public void ValidateAttributes_requires_department_and_clearance(string missing)
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "engineering",
            ["clearance"] = "internal",
        };
        attrs.Remove(missing);

        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary())
            .Should().ContainSingle().Which.Should().Contain($"必須属性 '{missing}'");
    }

    // 空白は「未設定」と同じに扱う（空文字を保存して必須を満たしたことにしない）。
    [Fact]
    public void ValidateAttributes_treats_blank_as_missing()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "   ",
            ["clearance"] = "internal",
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary())
            .Should().NotBeEmpty();
    }

    // 🔴 05_screens §SC-17: **タグは任意である**（過剰拒否の否定側）。
    // 必須を増やす変異はここで落ちる。
    [Fact]
    public void ValidateAttributes_does_not_require_optional_tags()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "finance",
            ["clearance"] = "internal",
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary()).Should().BeEmpty();
    }

    // 陽性対照: 任意属性を付けても通る。
    [Fact]
    public void ValidateAttributes_accepts_an_optional_tag_from_the_dictionary()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "finance",
            ["clearance"] = "internal",
            ["tags"] = "management",
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary()).Should().BeEmpty();
    }

    // 05_screens §SC-17: **SC-09 の属性体系に定義済みの値のみ。**
    [Fact]
    public void ValidateAttributes_rejects_values_outside_the_dictionary()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "engineering",
            ["clearance"] = "top-secret", // 辞書に無い
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary())
            .Should().ContainSingle().Which.Should().Contain("top-secret");
    }

    // 文書側（自由タグ許容）と**意図的に違う**: 辞書に無いキーも拒否する。
    // 受け付けて無視すると「割り当てたのに効かない」が黙って作れる。
    [Fact]
    public void ValidateAttributes_rejects_keys_that_the_user_dictionary_does_not_define()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "engineering",
            ["clearance"] = "internal",
            ["unknown_key"] = "whatever",
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary())
            .Should().ContainSingle().Which.Should().Contain("unknown_key");
    }

    // スコープを跨がない: 文書スコープの定義は利用者への割当を許可しない。
    [Fact]
    public void ValidateAttributes_does_not_borrow_document_scoped_definitions()
    {
        var attrs = new Dictionary<string, string>
        {
            ["department"] = "engineering",
            ["clearance"] = "internal",
            ["confidentiality"] = "public", // document スコープにしか定義が無い
        };
        UserAssignmentValidation.ValidateAttributes(attrs, Dictionary())
            .Should().ContainSingle().Which.Should().Contain("confidentiality");
    }

    // 辞書側が未整備なら「必須を諦める」のではなく断る ——
    // 統制を定めたことと効いていることの区別を残す。
    [Fact]
    public void ValidateAttributes_reports_when_the_dictionary_lacks_a_required_key()
    {
        List<AttributeDefinition> partial =
        [
            AttributeDefinition.Create("department", "所属部門", ["engineering"], false, AttributeScope.User),
        ];
        var attrs = new Dictionary<string, string> { ["department"] = "engineering" };

        UserAssignmentValidation.ValidateAttributes(attrs, partial)
            .Should().ContainSingle().Which.Should().Contain("属性辞書に定義されていません");
    }

    // 必須キーの宣言そのものを固定する（計画の「部門・機密区分上限」に対応する 2 キー）。
    [Fact]
    public void RequiredUserAttributeKeys_are_department_and_clearance()
        => UserAssignmentValidation.RequiredUserAttributeKeys
            .Should().BeEquivalentTo("department", "clearance");
}
