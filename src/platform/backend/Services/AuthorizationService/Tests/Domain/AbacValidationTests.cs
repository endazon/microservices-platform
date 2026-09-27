using AuthorizationService.Domain;
using AwesomeAssertions;
using AuthorizationService.Tests.Features.Authz.ResolveScope;

namespace AuthorizationService.Tests.Domain;

// FR-09, UC-05, ADR-0004: 属性辞書・ポリシー・文書属性バリデーションの単体テスト
[Trait("TestKind", "Unit")]
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

    // ---- 動的束縛（#1666） ----
    // FR-05, SC-09, 計画 ADR-0036 D-02・D-03・D-06, ADR-0121 決定 1: 束縛を置けるのは文書の条件の 2 か所だけ。
    // SC-09 の編集器が作る形（所有者・共有先）は通り、それ以外は保存の前に止まる。

    private static Dictionary<string, List<string>> Doc(string key, params string[] values) =>
        new() { [key] = [.. values] };

    // T-68（陽性対照）: 所有者の read ポリシー（ADR-0121 決定 1 の形）は通る。辞書に owner が無くてもよい。
    [Fact]
    public void ValidatePolicy_OwnerCurrentUserBinding_NoErrors()
    {
        var errors = AbacValidation.ValidatePolicy(
            "所有者は自分の文書を読める", "read",
            new Dictionary<string, List<string>>(),
            Doc("owner", "${current_user}"),
            [Confidentiality(), Clearance()]);

        errors.Should().BeEmpty();
    }

    // T-68（陽性対照）: 共有先の形（個人とグループ。ADR-0036 D-06）は通る。
    [Fact]
    public void ValidatePolicy_SharedWithBothBindings_NoErrors()
    {
        var errors = AbacValidation.ValidatePolicy(
            "共有された個人資料を読める", "read",
            new Dictionary<string, List<string>>(),
            Doc("shared_with", "${current_user}", "${current_groups}"),
            [Confidentiality(), Clearance()]);

        errors.Should().BeEmpty();
    }

    // T-69: 計画に無い束縛変数（綴り違い・大小違い）は拒否する（D-03。評価器はリテラルとして残し、静かに効かない）。
    [Theory]
    [InlineData("${current_usr}")]
    [InlineData("${Current_User}")]
    [InlineData("${current_department}")]
    public void ValidatePolicy_UnknownBindingVariable_Error(string value)
    {
        var errors = AbacValidation.ValidatePolicy(
            "綴り違い", "read",
            new Dictionary<string, List<string>>(),
            Doc("owner", value),
            [Confidentiality(), Clearance()]);

        errors.Should().ContainSingle(e => e.Contains("documentConditions.owner") && e.Contains(value));
    }

    // T-69: 位置の違う束縛は拒否する —— owner は利用者 1 人を指すので ${current_groups} を置けない。
    // 計画が束縛を置かない属性（confidentiality）にも置けない。
    [Theory]
    [InlineData("owner", "${current_groups}")]
    [InlineData("confidentiality", "${current_user}")]
    [InlineData("department", "${current_groups}")]
    public void ValidatePolicy_BindingAtUnplannedPosition_Error(string key, string value)
    {
        var errors = AbacValidation.ValidatePolicy(
            "位置違い", "read",
            new Dictionary<string, List<string>>(),
            Doc(key, value),
            [Confidentiality(), Clearance()]);

        errors.Should().Contain(e => e.Contains($"documentConditions.{key}") && e.Contains("動的束縛"));
    }

    // T-69: 利用者の条件に束縛は置けない（評価器は利用者の条件を束縛しない）。
    [Fact]
    public void ValidatePolicy_BindingInUserConditions_Error()
    {
        var errors = AbacValidation.ValidatePolicy(
            "利用者の条件の束縛", "read",
            new Dictionary<string, List<string>> { ["owner"] = ["${current_user}"] },
            new Dictionary<string, List<string>>(),
            [Confidentiality(), Clearance()]);

        errors.Should().ContainSingle(e => e.Contains("userConditions.owner"));
    }

    // T-70: 辞書に owner・shared_with が定義されていても、許した束縛は「辞書外の値」にしない（SC-09 の選択肢と整合）。
    // 陰性対照: 辞書に無いリテラルは従来どおり辞書外として拒否する。
    [Fact]
    public void ValidatePolicy_AllowedBindingNotRejectedAsOutsideDictionary()
    {
        var sharedWith = AttributeDefinition.Create(
            "shared_with", "共有先", ["group-sales"], required: false, AttributeScope.Document);

        AbacValidation.ValidatePolicy(
                "共有先", "read", [],
                Doc("shared_with", "${current_user}", "${current_groups}"),
                [sharedWith])
            .Should().BeEmpty();

        AbacValidation.ValidatePolicy(
                "共有先（辞書外）", "read", [],
                Doc("shared_with", "group-hr"),
                [sharedWith])
            .Should().ContainSingle(e => e.Contains("辞書外"));
    }

    // T-72（#1666 監査）: 束縛とリテラルは同じ値配列に混ぜられない。
    // `owner:[${current_user}, "bob"]` は全員に bob の文書を読ませる（計画の owner の位置は { ${current_user} } だけ）。
    // ［2026-09-28 / #1676］リテラルが先の並び（`shared_with:["bob", ${current_groups}]`）も同じく拒否する。
    // 混在の検査を先頭の値だけで判定する実装（先頭が束縛のときだけ混在を見る）が生き残っていた。
    [Theory]
    [InlineData("owner", "${current_user}", "bob", "bob")]
    [InlineData("shared_with", "${current_groups}", "group-sales", "group-sales")]
    [InlineData("shared_with", "bob", "${current_groups}", "bob")]
    public void ValidatePolicy_BindingMixedWithLiteral_Error(string key, string first, string second, string literal)
    {
        AbacValidation.ValidatePolicy(
                "混在", "read", [],
                Doc(key, first, second),
                [Confidentiality(), Clearance()])
            .Should().ContainSingle(e => e.Contains($"documentConditions.{key}") && e.Contains("混ぜる") && e.Contains(literal));
    }

    // T-73（#1666 監査）: 束縛を置ける action は計画の判定規則のとおり。
    // read は owner・shared_with、write は owner だけ（「共有先には書き込み権限を与えない」）。analyze・manage には無い。
    [Theory]
    [InlineData("write", "shared_with", "${current_user}")]
    [InlineData("write", "shared_with", "${current_groups}")]
    [InlineData("manage", "shared_with", "${current_user}")]
    [InlineData("analyze", "shared_with", "${current_groups}")]
    [InlineData("manage", "owner", "${current_user}")]
    [InlineData("analyze", "owner", "${current_user}")]
    public void ValidatePolicy_BindingOutsidePlannedAction_Error(string action, string key, string value)
    {
        AbacValidation.ValidatePolicy(
                "action 違い", action, [],
                Doc(key, value),
                [Confidentiality(), Clearance()])
            .Should().ContainSingle(e => e.Contains($"documentConditions.{key}") && e.Contains($"action={action}"));
    }

    // T-73（陽性対照）: write × owner（dev seed の所有者の書き込みの形。ADR-0036 D-07）は通る。
    [Fact]
    public void ValidatePolicy_WriteOwnerBinding_NoErrors()
    {
        AbacValidation.ValidatePolicy(
                "所有者は自分の文書を書ける", "write", null,
                Doc("owner", "${current_user}"),
                [Confidentiality(), Clearance()])
            .Should().BeEmpty();
    }

    // T-69（#1666 監査）: 前後に文字が付いた束縛は束縛の形のまま表の値と一致しないので拒否する。
    [Theory]
    [InlineData("x${current_user}")]
    [InlineData("${current_user} ")]
    public void ValidatePolicy_BindingWithSurroundingCharacters_Error(string value)
    {
        AbacValidation.ValidatePolicy(
                "前後の文字", "read", [],
                Doc("owner", value),
                [Confidentiality(), Clearance()])
            .Should().ContainSingle(e => e.Contains("documentConditions.owner") && e.Contains("置けません"));
    }

    // T-70: dev seed の全ポリシーが検証を通る（seed は同じ API で投入される。IADR-0133）。
    [Fact]
    public void ValidatePolicy_DevSeedPolicies_AllPass()
    {
        var policies = SeedScopeFixture.SeedPolicies();
        policies.Should().Contain(p => p.DocumentConditions.ContainsKey("owner"), "所有者の read ポリシーを含む seed を読んでいる");
        var dictionary = SeedAttributes();
        dictionary.Should().NotBeEmpty("seed の属性辞書を読んでいる");

        foreach (var p in policies)
        {
            // 辞書なし（保存時に辞書が空の環境）と、seed の属性辞書を入れた環境の両方で通る。
            AbacValidation.ValidatePolicy(p.Name, p.Action, p.UserConditions, p.DocumentConditions, [])
                .Should().BeEmpty($"seed のポリシー '{p.Name}' は検証を通る（辞書なし）");
            AbacValidation.ValidatePolicy(p.Name, p.Action, p.UserConditions, p.DocumentConditions, dictionary)
                .Should().BeEmpty($"seed のポリシー '{p.Name}' は検証を通る（seed の属性辞書あり）");
        }

        // seed の属性辞書そのものも、利用者属性の名前の検査（T-71）を通る。
        var entries = new List<AttributeDefinition>();
        foreach (var d in dictionary)
        {
            AbacValidation.ValidateAttributeDefinition(d.Key, d.Label, d.AllowedValues, d.Scope, entries,
                    allowedValuesDerived: DepartmentDictionaryValues.IsDerived(d.Key))
                .Should().BeEmpty($"seed の属性 '{d.Key}'（{d.Scope}）は登録できる");
            entries.Add(d);
        }
    }

    // T-70: 運用仕様書（§所有者の読み取りのポリシーの投入）の本文も検証を通る。本文は運用仕様書の JSON の写しである。
    [Fact]
    public void ValidatePolicy_OperationsOwnerReadBody_Passes()
    {
        AbacValidation.ValidatePolicy(
                "所有者は自分の文書を読める", "read",
                new Dictionary<string, List<string>>(),
                Doc("owner", "${current_user}"),
                SeedAttributes())
            .Should().BeEmpty();
    }

    private static List<AttributeDefinition> SeedAttributes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "local", "abac-seed", "attributes.json")))
            dir = dir.Parent;
        dir.Should().NotBeNull("リポジトリの根から deploy/local/abac-seed/attributes.json を探す");
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir!.FullName, "deploy", "local", "abac-seed", "attributes.json")));
        return [.. doc.RootElement.GetProperty("attributes").EnumerateArray().Select(a => AttributeDefinition.Create(
            a.GetProperty("key").GetString()!,
            a.GetProperty("label").GetString()!,
            [.. a.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()!)],
            a.GetProperty("required").GetBoolean(),
            a.GetProperty("scope").GetString()!))];
    }

    // T-71（#1666 レビュー）: 利用者スコープに束縛の位置と同名のキー（owner・shared_with）は登録できない。
    // 辞書のキーの同一性（一意の検査）と同じく大小を区別しない。
    [Theory]
    [InlineData("owner")]
    [InlineData("shared_with")]
    [InlineData("Owner")]
    public void ValidateAttributeDefinition_UserScopeBindingPositionKey_Error(string key)
    {
        var errors = AbacValidation.ValidateAttributeDefinition(
            key, "担当者", ["alice"], AttributeScope.User, []);

        errors.Should().ContainSingle(e => e.Contains($"key '{key}'") && e.Contains("利用者属性"));
    }

    // T-74（#1676）: 保存済みのキーの更新（Key / Scope は不変）では、利用者スコープの束縛の位置の名前を拒まない。
    // 拒否より前に登録された属性のラベル・許可値を直せるようにする。登録（既定）は T-71 のとおり拒む（否定の対照を同じ入力で置く）。
    [Theory]
    [InlineData("owner")]
    [InlineData("shared_with")]
    [InlineData("Owner")]
    public void ValidateAttributeDefinition_UserScopeBindingPositionKeyAlreadyStored_NoErrors(string key)
    {
        var stored = AttributeDefinition.Create(key, "担当者", ["alice"], false, AttributeScope.User);

        AbacValidation.ValidateAttributeDefinition(
                key, "担当者（改）", ["alice", "bob"], AttributeScope.User, [stored], excludeId: stored.Id,
                keyAlreadyStored: true)
            .Should().BeEmpty();
        AbacValidation.ValidateAttributeDefinition(
                key, "担当者（改）", ["alice", "bob"], AttributeScope.User, [stored], excludeId: stored.Id)
            .Should().ContainSingle(e => e.Contains("利用者属性"), "登録（既定）では拒む");
    }

    // T-71（陽性対照）: 文書スコープの同名は登録できる（束縛の値と辞書の許可値を併せ持てる）。
    // 利用者スコープの他のキーは従来どおり通る。
    [Theory]
    [InlineData("owner", AttributeScope.Document)]
    [InlineData("shared_with", AttributeScope.Document)]
    [InlineData("owner_team", AttributeScope.User)]
    public void ValidateAttributeDefinition_BindingPositionKeyOutsideUserScope_NoErrors(string key, string scope)
    {
        AbacValidation.ValidateAttributeDefinition(key, "ラベル", ["x"], scope, [])
            .Should().BeEmpty();
    }

    // T-69（#1666 レビュー）: 束縛の位置のキーは大小を区別する。`Owner` の束縛は拒否する
    // （文書の属性の突き合わせは大小を区別するので、`Owner ∈ {${current_user}}` は静かに効かない）。
    [Theory]
    [InlineData("Owner", "${current_user}")]
    [InlineData("SHARED_WITH", "${current_groups}")]
    public void ValidatePolicy_BindingPositionKeyIsCaseSensitive_Error(string key, string value)
    {
        AbacValidation.ValidatePolicy(
                "大小違いのキー", "read", [],
                Doc(key, value),
                [Confidentiality(), Clearance()])
            .Should().ContainSingle(e => e.Contains($"documentConditions.{key}") && e.Contains("動的束縛"));
    }
}
