using AuthorizationService.Domain;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Domain;

// FR-05, ADR-0004: ABAC ポリシー評価（deny-by-default ＋ 多値 allow-list）の単体テスト
[Trait("TestKind", "Unit")]
public class AbacEvaluatorTests
{
    private static AbacPolicy ReadPolicy(
        Dictionary<string, List<string>> userCond,
        Dictionary<string, List<string>> docCond) =>
        AbacPolicy.Create("p", PolicyAction.Read, userCond, docCond);

    // FR-05: 利用者にマッチするポリシーが無ければ Granted=false（deny-by-default）
    [Fact]
    public void ResolveScope_NoMatchingPolicy_NotGranted()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "sales" });
        var policies = new[]
        {
            ReadPolicy(new() { ["department"] = ["engineering"] },
                       new() { ["department"] = ["engineering"] })
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse("利用者条件に合致するポリシーが無い");
        result.AllowedFilters.Should().BeEmpty();
    }

    // FR-05: マッチするポリシーがあれば Granted=true、文書条件がフィルタになる
    [Fact]
    public void ResolveScope_MatchingPolicy_GrantedWithFilters()
    {
        var req = new AccessScopeRequest("u2", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            ReadPolicy(new() { ["department"] = ["engineering"] },
                       new() { ["confidentiality"] = ["public", "internal"] })
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue();
        result.AllowedFilters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().BeEquivalentTo("public", "internal");
    }

    // FR-05: 複数ポリシーがマッチした場合、同一キーの許可値は union（OR拡張）される
    [Fact]
    public void ResolveScope_MultiplePolicies_UnionsAllowedValues()
    {
        var req = new AccessScopeRequest("u3", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            ReadPolicy(new() { ["department"] = ["engineering"] },
                       new() { ["confidentiality"] = ["public"] }),
            ReadPolicy(new() { ["department"] = ["engineering"] },
                       new() { ["confidentiality"] = ["internal"] })
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue();
        result.AllowedFilters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().BeEquivalentTo("public", "internal");
    }

    // FR-05: マッチするが文書条件が空のポリシー = 条件無しで許可（Granted=true・フィルタ空＝全件可）
    [Fact]
    public void ResolveScope_MatchingPolicyWithoutDocConditions_GrantedWithoutFilters()
    {
        var req = new AccessScopeRequest("admin", new() { ["role"] = "admin" });
        var policies = new[]
        {
            ReadPolicy(new() { ["role"] = ["admin"] }, new())
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue();
        result.AllowedFilters.Should().BeEmpty();
    }

    // ---- FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 段 2: 名前つき分岐の組み立て --------
    //
    // 🔴 **本段は「緑」が成功の証拠にならない段である。**
    // tripwire（GraphService の AbacUnenforcedAxisTests）は消費側が未対応のため緑のままで、
    // 既存テストの全緑も「退行が無い」までしか意味しない。
    // **下のテスト群だけが、分岐が実際に組み上がっていることの証拠である。**
    //
    // 🔴 **肯定側には陰性対照を対で置いてある。**「常に 2 本返す実装」は 1 を通すが 2・3 で落ち、
    // 「本数は合うが中身が同じ」実装は 4 で落ちる。

    private static AbacPolicy NamedReadPolicy(
        string name,
        Dictionary<string, List<string>> userCond,
        Dictionary<string, List<string>> docCond) =>
        AbacPolicy.Create(name, PolicyAction.Read, userCond, docCond);

    // 1: 2 つのポリシーが同時にマッチしたら分岐は 2 本になる（＝選言が表現できている）。
    [Fact]
    public void ResolveScope_TwoMatchingPolicies_ProducesTwoBranches()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("組織文書", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
            NamedReadPolicy("個人資料", new() { ["department"] = ["engineering"] },
                            new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Branches.Should().HaveCount(2,
            "read 規則は 3 節の選言であり、単一の連言へ潰すと『両方を満たす文書』しか見えない");
    }

    // 2（陰性対照）: 片方しかマッチしなければ分岐は 1 本。「常に 2 本返す」実装を落とす。
    [Fact]
    public void ResolveScope_OnlyOnePolicyMatches_ProducesOneBranch()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "sales" });
        var policies = new[]
        {
            NamedReadPolicy("組織文書", new() { ["department"] = ["sales"] },
                            new() { ["confidentiality"] = ["internal"] }),
            NamedReadPolicy("開発部限定", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["restricted"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Branches.Should().ContainSingle();
        result.Branches![0].Name.Should().Be("組織文書");
    }

    // 3（陰性対照）: 1 つもマッチしなければ deny-by-default。分岐は 0 本。
    [Fact]
    public void ResolveScope_NoMatchingPolicy_ProducesNoBranches()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "legal" });
        var policies = new[]
        {
            NamedReadPolicy("開発部限定", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse("deny-by-default は変えていない");
        result.Branches.Should().BeEmpty();
    }

    // 🔴 4: 本数だけでなく**中身**を確かめる。「2 本返すが中身が同じ」実装を落とす。
    [Fact]
    public void ResolveScope_Branches_CarryDistinctNamesAndFilters()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("組織文書", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
            NamedReadPolicy("個人資料", new() { ["department"] = ["engineering"] },
                            new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        var org = result.Branches!.Single(b => b.Name == "組織文書");
        var mine = result.Branches!.Single(b => b.Name == "個人資料");

        org.Filters.Should().ContainSingle()
            .Which.Key.Should().Be("confidentiality");
        mine.Filters.Should().ContainSingle()
            .Which.Key.Should().Be("owner");

        org.Filters.Should().NotBeEquivalentTo(mine.Filters,
            "分岐が 2 本あっても中身が同じなら選言を表現できていない");
    }

    // 5: ${current_user} は**分岐の中で**主体へ束縛される（IADR-0253 決定 3）。
    [Fact]
    public void ResolveScope_BindsCurrentUserPlaceholder_InsideBranches()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("個人資料", new() { ["department"] = ["engineering"] },
                            new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo("alice");
    }

    // 🔴 6（陰性対照・意図した非対称）: AllowedFilters では束縛**しない**。
    //
    // 束縛すると未移行の消費側が `owner ∈ {alice}` AND `confidentiality ∈ {internal}` という
    // **壊れた連言**で判定してしまう。リテラルのまま残せばどの文書にも一致せず deny 側へ倒れる。
    // IADR-0253 決定 2「AllowedFilters は算出アルゴリズムごと据え置く」の帰結である。
    [Fact]
    public void ResolveScope_DoesNotBindPlaceholder_InAllowedFilters()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("個人資料", new() { ["department"] = ["engineering"] },
                            new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.AllowedFilters.Single().AllowedValues.Should().BeEquivalentTo(
            ["${current_user}"],
            "未移行の消費側は据え置きの面を読む。束縛すると壊れた連言で判定してしまう");
    }

    // 7（回帰）: AllowedFilters の算出は従来どおり（キー単位 union）。分岐の導入で変えていない。
    [Fact]
    public void ResolveScope_AllowedFilters_StillUnionsByKey_Unchanged()
    {
        var req = new AccessScopeRequest("u3", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("公開", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["public"] }),
            NamedReadPolicy("社内", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.AllowedFilters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().BeEquivalentTo("public", "internal");
        // 同じ入力で分岐は 2 本になる（据え置きの面と新しい面が別々に立つ）。
        result.Branches.Should().HaveCount(2);
    }

    // 8（陰性対照）: 束縛するのは ${current_user} だけ。未知のプレースホルダはそのまま残す。
    [Fact]
    public void ResolveScope_LeavesUnknownPlaceholdersUntouched()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("未知の束縛", new() { ["department"] = ["engineering"] },
                            new() { ["department"] = ["${current_department}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo(
                ["${current_department}"],
                "計画が束縛変数の語彙を定めていないため、実装が先取りして増やさない");
    }

    // ── FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0036 D-03・D-06, ADR-0098 決定 1,
    //    [[IADR-0447]] (#1447): `${current_groups}` の束縛（受け入れ基準 1）────────────
    //
    // 🔴 認可を**広げる**変更なので、陽性（所属が効く）と陰性（所属が無ければ効かない・
    // 語彙は増えない）を対で置く。片方だけでは「常に全グループを許す」実装と区別できない。

    // 9: `${current_groups}` は所属の集合へ**展開される**（0..N 値）。
    [Fact]
    public void ResolveScope_ExpandsCurrentGroupsPlaceholder_IntoTheMembershipSet()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("共有された資料", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge", "g-finance" });

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo("g-knowledge", "g-finance");
    }

    // 10: 計画 07_abac-attribute-model §動的束縛の判定規則
    // `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅` —— **1 つのフィルタに
    // 主体と所属が同居する**（個人共有とグループ共有は同じ分岐で効く）。
    [Fact]
    public void ResolveScope_BindsBothCurrentUserAndCurrentGroups_InOneFilter()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("共有された資料", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_user}", "${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge" });

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo("alice", "g-knowledge");
    }

    // 11: 🔴 所属が無ければ `shared_with` の分岐は **`${current_user}` だけ**になる
    // （グループ共有の分は 1 つも生えない ＝ 他人のグループ共有へ到達しない）。
    [Fact]
    public void ResolveScope_WithoutMemberships_LeavesOnlyTheCurrentUser()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("共有された資料", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_user}", "${current_groups}"] }),
        };

        // 所属なし（空集合）と**渡し忘れ（null）は同値**である（実装の注記）。
        foreach (var groups in new IReadOnlySet<string>?[] { null, new HashSet<string>() })
        {
            var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read, groups);

            result.Branches!.Single().Filters.Single().AllowedValues
                .Should().BeEquivalentTo(["alice"],
                    "所属が無い主体に他人のグループ共有が見えてはならない");
        }
    }

    // 12: 🔴 `${current_groups}` **しか**無い条件で所属が空なら、**分岐ごと落ちる**。
    //
    // フィルタだけ落ちる（＝連言が空になる）形にすると、消費側は「分岐のフィルタが空 ＝
    // そのポリシーの範囲で全件許可」と読む（`BffScopeResolver` / `AbacPageFilter` の契約）——
    // **所属が無い主体に全件が見える**。倒す向きを間違えると最悪の壊れ方をする箇所である。
    [Fact]
    public void ResolveScope_DropsTheWholeBranch_WhenBindingEmptiesAFilter()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("グループ共有のみ", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read, groups: null);

        result.Branches.Should().BeEmpty(
            "許可値が空のフィルタを残すと、消費側が「無条件許可」と読む余地が生まれる");
        // 🔴 **`Granted` は true のままである**（マッチしたポリシーは在った）。それでも
        // 据え置きの `AllowedFilters` はリテラルのままなので、どの文書にも一致しない ＝ deny 側。
        result.Granted.Should().BeTrue();
        result.AllowedFilters.Single().AllowedValues.Should().BeEquivalentTo(["${current_groups}"]);
    }

    // 13: 陽性対照（12 の対）: **同じ入力で所属が 1 つあれば分岐は立つ。**
    // 「常に分岐を落とす」実装は 12 だけを通してしまう。
    [Fact]
    public void ResolveScope_KeepsTheBranch_WhenAtLeastOneGroupIsBound()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("グループ共有のみ", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge" });

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo("g-knowledge");
    }

    // 14: 🔴 **落ちるのは空になった分岐だけである**（他の分岐は残る）。
    // 分岐間は OR なので、1 本落ちても他の許可は生きていなければならない。
    [Fact]
    public void ResolveScope_DroppingOneBranch_DoesNotAffectTheOthers()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("組織文書", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
            NamedReadPolicy("グループ共有のみ", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read, groups: null);

        result.Branches.Should().ContainSingle().Which.Name.Should().Be("組織文書");
    }

    // 15: 🔴 陰性対照（**AllowedFilters では束縛しない**。#989 / IADR-0253 決定 2 の据え置き）。
    // `${current_groups}` でも `${current_user}` と同じ非対称を保つ。
    [Fact]
    public void ResolveScope_DoesNotBindCurrentGroups_InAllowedFilters()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("共有された資料", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["${current_user}", "${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge" });

        result.AllowedFilters.Single().AllowedValues.Should().BeEquivalentTo(
            ["${current_user}", "${current_groups}"],
            "未移行の消費側は据え置きの面を読む。束縛すると壊れた連言で判定してしまう");
    }

    // 16: 🔴 陰性対照（語彙は 2 つのままである）: 所属を渡しても未知のプレースホルダは残る。
    // **`${current_department}` は束縛しない** —— 計画 `ADR-0036` D-03 が定めた 2 つだけである。
    [Fact]
    public void ResolveScope_StillLeavesUnknownPlaceholdersUntouched_WhenGroupsAreBound()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("未知の束縛", new() { ["department"] = ["engineering"] },
                            new() { ["department"] = ["${current_department}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge" });

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().BeEquivalentTo(
                ["${current_department}"],
                "束縛変数の語彙は計画が定める。実装が 3 つ目を先取りしない");
    }

    // 17: 所属と同名のリテラルが混ざっても**重複しない**（許可集合は集合である）。
    [Fact]
    public void ResolveScope_DoesNotDuplicateValues_WhenALiteralEqualsABoundGroup()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("共有された資料", new() { ["department"] = ["engineering"] },
                            new() { ["shared_with"] = ["g-knowledge", "${current_groups}"] }),
        };

        var result = AbacEvaluator.ResolveScope(
            req, policies, PolicyAction.Read,
            new HashSet<string>(StringComparer.Ordinal) { "g-knowledge", "g-finance" });

        result.Branches!.Single().Filters.Single().AllowedValues
            .Should().Equal("g-knowledge", "g-finance");
    }

    // ---- FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）段 5: Action の解決 ----
    //
    // 🔴 認可の変更なので、否定形（許してはならないものが通らない）と陽性対照を対で置く。
    // 「常に空スコープを返す実装」は否定形だけを通す——陽性対照が無い否定形は証拠にならない。

    // FR-21, ADR-0036 D-07: 契約の既定値リテラル "read" は PolicyAction.Read と一致する。
    // 契約プロジェクトはドメイン型を参照できないためリテラルで持つ——ズレたら既存呼び出し元の
    // 全てが「どのポリシーにもマッチしない action」で全件遮断へ落ちる。ここで固定する。
    [Fact]
    public void AccessScopeRequest_DefaultAction_MatchesPolicyActionRead()
    {
        var req = new AccessScopeRequest("u1", new());

        req.Action.Should().Be(PolicyAction.Read);
    }

    // FR-21, IADR-0253 決定 5（陽性対照）: write ポリシーがあれば write スコープが分岐つきで返る。
    // ${current_user} の束縛は read と同じく分岐の中で解決される。
    [Fact]
    public void ResolveScope_WriteAction_ResolvesWritePolicies()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            AbacPolicy.Create("所有者の書き込み", PolicyAction.Write,
                new() { ["department"] = ["engineering"] },
                new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Write);

        result.Granted.Should().BeTrue();
        result.Branches.Should().ContainSingle()
            .Which.Filters.Single().AllowedValues.Should().BeEquivalentTo("alice");
    }

    // FR-21, FR-05（否定形・2 と対）: write ポリシーが 1 件も無ければ write スコープは全件遮断。
    // PolicyAction へ write を足したこと自体は何も許可しない（deny-by-default）。
    [Fact]
    public void ResolveScope_WriteAction_WithoutWritePolicies_NotGranted()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            // read を広く許すポリシーが在っても、write には 1 ビットも効かない。
            NamedReadPolicy("組織文書", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Write);

        result.Granted.Should().BeFalse(
            "read の許可が write へ漏れると「読めるなら書ける」になる——#993 が固定した欠陥の逆流");
        result.Branches.Should().BeEmpty();
        result.AllowedFilters.Should().BeEmpty();
    }

    // FR-05（否定形・アクション分離の対）: read のスコープに write ポリシーが混ざらない。
    [Fact]
    public void ResolveScope_ReadAction_DoesNotIncludeWritePolicies()
    {
        var req = new AccessScopeRequest("alice", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("組織文書", new() { ["department"] = ["engineering"] },
                            new() { ["confidentiality"] = ["internal"] }),
            AbacPolicy.Create("所有者の書き込み", PolicyAction.Write,
                new() { ["department"] = ["engineering"] },
                new() { ["owner"] = ["${current_user}"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read);

        result.Branches.Should().ContainSingle(
            "write ポリシーの owner 分岐が read へ混ざると閲覧範囲が変わってしまう");
        result.Branches!.Single().Name.Should().Be("組織文書");
    }

    // ---- #1242 / IADR-0384: 「confidentiality フィルタが無いスコープ」が実在することの固定 ----
    //
    // 🔴 **本テストは消費側（MCP の登録者属性解決）の陰性対照が机上の作り物でないことの担保である。**
    // ADR-0036 D-01 は `read` 許可を「属性ベース ∨ **所有者ベース** ∨ 共有先ベース」の選言と定め、
    // 所有者ベースのポリシーは典型的に `userConditions` を持たない。`MatchesUserConditions` は
    // 条件なしを**全利用者マッチ**として扱うため、`clearance` を持たない利用者にも
    // このポリシー**だけ**がマッチする。そのとき返るスコープは
    // **`Granted=true` かつ `confidentiality` フィルタ無し**である。
    //
    // **この形を「無制限」と読むと、登録者が `restricted` の無人アカウントを作れる**
    // （McpServer 側 `AuthorizationServiceRegistrarAttributesTests` の陰性対照 3 本）。
    // 現 seed の read ポリシーは 4 本とも階段（下の陽性対照）なので**今日は発現しない**。
    [Fact]
    public void ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter()
    {
        // `clearance` を持たない利用者。階段ポリシーには 1 本もマッチしない。
        var req = new AccessScopeRequest("u1", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            // ADR-0036 D-01・D-02: 所有者ベースの read（利用者条件なし・`${current_user}` 束縛）。
            NamedReadPolicy("所有者は自分の文書を読める", userCond: [],
                new() { ["owner"] = ["${current_user}"] }),
            // 陽性対照: 階段ポリシーは存在するが、この利用者にはマッチしない
            // （＝「ポリシーが 1 本しか無い作り物」ではないことの担保）。
            NamedReadPolicy("internal 取扱者", new() { ["clearance"] = ["internal"] },
                new() { ["confidentiality"] = ["public", "internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue("所有者ポリシーは利用者条件を持たないので全利用者にマッチする");
        result.Branches.Should().ContainSingle().Which.Name.Should().Be("所有者は自分の文書を読める");
        result.Branches!.Single().Filters.Should().ContainSingle()
            .Which.Key.Should().Be("owner", "分岐は owner だけを条件に持つ");
        result.AllowedFilters.Should().NotContain(f => f.Key == "confidentiality",
            "🔴 これが #1242 の入力である —— **不在であって『無制限』ではない**");
    }

    // ---- IADR-0253 決定 2 への追記（2026-08-23 / #989）: キー単位 union の混成（現状固定） ----
    //
    // 🔴 **本テストは既存挙動の記録であり、望ましい挙動の主張ではない。**
    // AllowedFilters のキー単位 union は、複数ポリシーが同じキー集合を別の値で条件づけたとき、
    // **どのポリシー単独も許可しない値の混成**（下の例では internal × sales）を許す。
    // 「AllowedFilters は分岐の和の部分集合」という決定 2 の根拠には、この反例がある。
    // 現在の実効的な認可軸は confidentiality 1 本（#516）でありキーが 1 つなら混成は起きないため
    // 実データでは漏れないが、**複数キーのポリシー運用を始める前に段 3 の全サービス移行を終える**
    // 必要がある。据え置き（決定 2）を外す判断のときは、このテストを削除ではなく
    // 「混成が許可されないこと」の検証へ書き換えること。
    [Fact]
    public void AllowedFilters_KeyUnion_CanGrantCrossPolicyMixture_CurrentBehaviour()
    {
        var req = new AccessScopeRequest("u1", new() { ["department"] = "engineering" });
        var policies = new[]
        {
            NamedReadPolicy("A", new() { ["department"] = ["engineering"] },
                new() { ["confidentiality"] = ["internal"], ["department"] = ["hr"] }),
            NamedReadPolicy("B", new() { ["department"] = ["engineering"] },
                new() { ["confidentiality"] = ["public"], ["department"] = ["sales"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        // 混成文書 (internal, sales): どちらのポリシー単独でも許可されない＝分岐評価では不可視。
        var mixture = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["department"] = "sales",
        };
        var matchesBranch = result.Branches!.Any(b => b.Filters.All(f =>
            mixture.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase)));
        matchesBranch.Should().BeFalse("分岐の意味論はポリシー単位——混成はどの分岐も満たさない");

        // 据え置きの AllowedFilters（キー単位 union の連言）は同じ文書を許可してしまう（現状の記録）。
        var matchesUnion = result.AllowedFilters.All(f =>
            mixture.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
        matchesUnion.Should().BeTrue(
            "ここが false になったら AllowedFilters の算出が変わっている——決定 2 の据え置きが破れている合図");
    }

    // ---- #1324: 「利用者条件が空＝全利用者にマッチ」を両方向で固定する ------------------------
    //
    // FR-05, ADR-0004, ADR-0036 D-01: `read` 許可は「属性ベース ∨ **所有者ベース** ∨ 共有先ベース」の
    // 選言であり、所有者ベースのポリシーは利用者条件を持たない。だから
    // `MatchesUserConditions` の「条件が空なら全利用者にマッチ」は**意図された規則**である。
    //
    // 🔴 **この規則は両方向へ壊れ得るので、対で固定する。**
    //   ・寛容側の反転（「空条件は誰にもマッチしない」）→ 所有者が自分の文書を読めなくなる
    //   ・制限側の反転（「常に true」）→ deny-by-default（FR-05）が消える
    //
    // 🔴 **`conditions is null` を突く変異は等価変異である**（#1324 で実測）。
    // `AbacEntities.cs:47` が `UserConditions` を**非 null 型**として宣言し、`:64`（Create）と
    // `:75`（Update）が `userCond ?? []` で正規化するため、**null は保存されない**。
    // 唯一の呼び出し元（`AbacEvaluator.cs:26`）が渡すのも `policy.UserConditions` である。
    // よって null 分岐は到達不能であり、**そこを突く変異が緑で通ることは「無試験」を意味しない**。
    // 到達可能な入力は**空辞書**であり、下の 2 本はその側を固定する。
    // null → 空辞書の正規化は `AbacValidationTests.AbacPolicy_Create_NullConditions_StoredAsEmpty` が持つ。
    //
    // 既存の `ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter`（上）も
    // 寛容側の変異で落ちるが、**あの試験の主題は #1242 の「不在フィルタ」**であって本規則ではない。
    // #1242 側の都合で書き換わると本規則の反転が無言で通るため、ここに**直接の主張**を置く。

    // FR-05, ADR-0004, ADR-0036 D-01（寛容側）: 利用者条件が空のポリシーは、
    // **属性を 1 つも持たない**利用者にもマッチする。
    [Fact]
    public void ResolveScope_EmptyUserConditions_GrantsToUserWithNoAttributes()
    {
        // 属性ゼロの利用者。階段ポリシーには定義上 1 本もマッチしない。
        var req = new AccessScopeRequest("u1", new());
        var policies = new[]
        {
            // ADR-0036 D-01/D-02: 所有者ベースの read（利用者条件なし・`${current_user}` 束縛）。
            NamedReadPolicy("所有者は自分の文書を読める", userCond: [],
                new() { ["owner"] = ["${current_user}"] }),
            // 陽性対照: 利用者条件を持つポリシーが同居しているが、この利用者にはマッチしない
            // （＝「ポリシーが 1 本しか無い作り物」でも「常に true」でもないことの担保）。
            NamedReadPolicy("internal 取扱者", new() { ["clearance"] = ["internal"] },
                new() { ["confidentiality"] = ["public", "internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue(
            "利用者条件が空のポリシーは全利用者にマッチする——所有者ベース read の前提である");
        result.Branches.Should().ContainSingle(
            "条件を持つ側は属性ゼロの利用者にマッチしてはならない")
            .Which.Name.Should().Be("所有者は自分の文書を読める");
        result.AllowedFilters.Should().ContainSingle()
            .Which.Key.Should().Be("owner");
    }

    // FR-05, ADR-0004（制限側・上の対）: 利用者条件を持つポリシーは、属性が合わない利用者へ許可しない。
    // キーが無い経路（`AbacEvaluator.cs:78`）と値が合わない経路（`:80`）の**両方**を通す。
    [Theory]
    [InlineData("department", "engineering", "キーが無い（clearance を持たない）")]
    [InlineData("clearance", "public", "キーはあるが値が合わない")]
    public void ResolveScope_UserConditionsPresent_DoesNotGrantWhenAttributesDiffer(
        string attrKey, string attrValue, string why)
    {
        var req = new AccessScopeRequest("u2", new() { [attrKey] = attrValue });
        var policies = new[]
        {
            NamedReadPolicy("internal 取扱者", new() { ["clearance"] = ["internal"] },
                new() { ["confidentiality"] = ["public", "internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse(why + "——FR-05 の deny-by-default");
        result.Branches.Should().BeEmpty(why);
        result.AllowedFilters.Should().BeEmpty(why);
    }

    // ---- #1323 / ADR-0080 決定 2: 集合値の利用者属性は「交差が空でないこと」でマッチする ------
    //
    // 計画の裁定（ADR-0080 決定 2）は **交差**であって部分集合ではない。
    // 「タグを 1 つ足しただけで既存のアクセスが失われる」振る舞いを明示的に退けている。
    // 🔴 ADR-0062 決定 2 の**部分集合**判定と混同しない —— あちらは**属性割当の統制**であり、
    // 向きが逆（狭める）である。ここはアクセス判定（広げる・OR）である。
    //
    // 🔴 **4 本は対で読む。** 陽性 1 本だけだと「集合値キーは常に true」で通ってしまい、
    // 陰性だけだと「常に false」で通ってしまう。単値の回帰も併せて置く
    // （一律に分割する実装は `clearance` の階段を静かに壊す。IADR-0385 の禁則）。

    // FR-05, FR-09, ADR-0080 決定 2（陽性）: 利用者のタグ集合と許容値集合の交差が 1 つでもあれば通る。
    [Fact]
    public void ResolveScope_SetValuedUserAttribute_MatchesWhenIntersectionIsNotEmpty()
    {
        // 線上表現（UserAttributeEncoding.Separator）で運ばれてくる。
        var req = new AccessScopeRequest("u1", new() { ["tags"] = "sales,hr" });
        var policies = new[]
        {
            NamedReadPolicy("営業資料", new() { ["tags"] = ["sales"] },
                new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeTrue("sales は交差する（部分集合であることは要求しない）");
        result.Branches.Should().ContainSingle().Which.Name.Should().Be("営業資料");
    }

    // FR-05, ADR-0080 決定 2（陰性対照 1）: 交差が空なら通らない。
    // 🔴 これが無いと「集合値キーは常に true」という縮退実装が陽性テストを通してしまう。
    [Fact]
    public void ResolveScope_SetValuedUserAttribute_DoesNotMatchWhenIntersectionIsEmpty()
    {
        var req = new AccessScopeRequest("u2", new() { ["tags"] = "legal,finance" });
        var policies = new[]
        {
            NamedReadPolicy("営業資料", new() { ["tags"] = ["sales"] },
                new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse("交差が空である——FR-05 の deny-by-default");
        result.Branches.Should().BeEmpty();
    }

    // FR-05, ADR-0080 決定 3（陰性対照 2）: 集合値キーでも「属性を持たない」はマッチしない。
    [Fact]
    public void ResolveScope_SetValuedUserAttribute_DoesNotMatchWhenTheAttributeIsAbsent()
    {
        var req = new AccessScopeRequest("u3", new() { ["clearance"] = "internal" });
        var policies = new[]
        {
            NamedReadPolicy("営業資料", new() { ["tags"] = ["sales"] },
                new() { ["confidentiality"] = ["internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse("tags を持たない利用者はマッチしない（ADR-0080 決定 3）");
    }

    // FR-05, IADR-0385 の禁則（回帰の担保）: 単値キーは分割しない。
    // 🔴 一律に分割すると `clearance: "internal"` が要素として扱われるだけでは済まず、
    // 区切り文字を含む値が**辞書外の要素**として通り得る。**階段ポリシーが静かに壊れる。**
    [Fact]
    public void ResolveScope_SingleValuedUserAttribute_IsNotSplitIntoASet()
    {
        // 「internal,restricted」という 1 つの値（そんな clearance は辞書に無い）。
        var req = new AccessScopeRequest("u4", new() { ["clearance"] = "internal,restricted" });
        var policies = new[]
        {
            NamedReadPolicy("internal 取扱者", new() { ["clearance"] = ["internal"] },
                new() { ["confidentiality"] = ["public", "internal"] }),
        };

        var result = AbacEvaluator.ResolveScope(req, policies);

        result.Granted.Should().BeFalse(
            "単値キーを分割すると、辞書に無い合成値が『internal を含む』として通ってしまう");
    }

    // FR-05, ADR-0080 決定 2（大文字小文字・区切りの揺れ）: 分割規則は契約 1 か所に従う。
    [Theory]
    [InlineData("SALES,hr", "大文字小文字は区別しない")]
    [InlineData("sales hr", "空白でも切る（UserAttributeEncoding.SplitOrdered の規則）")]
    [InlineData(" sales , hr ", "前後の空白は落とす")]
    public void ResolveScope_SetValuedUserAttribute_UsesTheContractSplittingRule(string carried, string why)
    {
        var req = new AccessScopeRequest("u5", new() { ["tags"] = carried });
        var policies = new[]
        {
            NamedReadPolicy("営業資料", new() { ["tags"] = ["sales"] },
                new() { ["confidentiality"] = ["internal"] }),
        };

        AbacEvaluator.ResolveScope(req, policies).Granted.Should().BeTrue(why);
    }

    // FR-05, FR-09, ADR-0080 決定 2（**`projects` でも同じ意味論であることの直接の担保**）:
    // 🔴 **`tags` で通っているから `projects` も通るはず、を推論で済ませない。**
    // 評価器の分岐は `UserAttributeEncoding.IsSetValued(key)` で一般化してあるが、
    // **一般化されていることそのものを固定する試験が要る**（キーを名指しで並べる実装への退行を止める）。
    //
    // `projects` は dev seed の属性辞書へ**入れていない**（ADR-0085 決定 1・2 の保留を辞書で守る。
    // [[IADR-0411]] 決定 5）が、**評価器は辞書と独立に動く**ため、ここでは直接評価できる。
    [Theory]
    [InlineData("tags")]
    [InlineData("projects")]
    public void ResolveScope_EverySetValuedKey_UsesIntersectionSemantics(string key)
    {
        UserAttributeEncoding.IsSetValued(key).Should().BeTrue("陽性対照: 契約が集合値と宣言している");

        var policies = new[]
        {
            NamedReadPolicy("集合条件", new() { [key] = ["alpha"] },
                new() { ["confidentiality"] = ["internal"] }),
        };

        // 陽性: 交差が空でない。
        AbacEvaluator.ResolveScope(
            new AccessScopeRequest("u1", new() { [key] = "alpha,beta" }), policies)
            .Granted.Should().BeTrue($"{key} は交差でマッチする");

        // 陰性対照 1: 交差が空。
        AbacEvaluator.ResolveScope(
            new AccessScopeRequest("u2", new() { [key] = "beta,gamma" }), policies)
            .Granted.Should().BeFalse($"{key} の交差が空なら deny");

        // 陰性対照 2: 属性そのものが無い（ADR-0080 決定 3）。
        AbacEvaluator.ResolveScope(
            new AccessScopeRequest("u3", new() { ["clearance"] = "internal" }), policies)
            .Granted.Should().BeFalse($"{key} を持たない利用者はマッチしない");
    }
}
