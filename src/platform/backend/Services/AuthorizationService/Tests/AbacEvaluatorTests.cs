using AuthorizationService.Domain;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests;

// FR-05, ADR-0004: ABAC ポリシー評価（deny-by-default ＋ 多値 allow-list）の単体テスト
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
}
