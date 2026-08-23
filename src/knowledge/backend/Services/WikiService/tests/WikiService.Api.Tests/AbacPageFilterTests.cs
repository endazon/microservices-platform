using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Tests;

// FR-13, FR-05, UC-07: Wiki ページ ABAC フィルタの評価意味論（AND / OR / deny-by-default）。
public class AbacPageFilterTests
{
    private static WikiPage Page(Dictionary<string, string> attrs)
        => WikiPage.CreateFromDocument(Guid.NewGuid(), "T", null, attrs, []);

    // deny-by-default: マッチするポリシーが無い（Granted=false）なら不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenNotGranted()
    {
        var page = Page(new() { ["confidentiality"] = "public" });
        var scope = new AccessScopeResponse("u", [], Granted: false);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // Granted かつ条件無し（全件可）は可視。
    [Fact]
    public void Matches_ReturnsTrue_WhenGrantedAndNoFilters()
    {
        var page = Page(new() { ["confidentiality"] = "confidential" });
        var scope = new AccessScopeResponse("u", [], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeTrue();
    }

    // 値集合内は OR: 許可値のいずれかに一致すれば可視。
    [Fact]
    public void Matches_ReturnsTrue_WhenValueInAllowedSet()
    {
        var page = Page(new() { ["department"] = "sales" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["hr", "sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeTrue();
    }

    // 値が許可集合外なら不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenValueNotAllowed()
    {
        var page = Page(new() { ["department"] = "legal" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["hr", "sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // フィルタ間は AND: 全キーを満たさなければ不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenAnyFilterFails()
    {
        var page = Page(new() { ["department"] = "sales", ["confidentiality"] = "restricted" });
        var scope = new AccessScopeResponse("u",
        [
            new AttributeFilter("department", ["sales"]),
            new AttributeFilter("confidentiality", ["public", "internal"])
        ], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // 属性キーを持たない文書は不一致（欠落は安全側）。
    [Fact]
    public void Matches_ReturnsFalse_WhenAttributeKeyMissing()
    {
        var page = Page(new() { ["confidentiality"] = "public" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // Filter は Granted=false で空を返す（deny-by-default）。
    [Fact]
    public void Filter_ReturnsEmpty_WhenNotGranted()
    {
        var pages = new[] { Page(new() { ["x"] = "1" }), Page(new() { ["x"] = "2" }) };
        var scope = new AccessScopeResponse("u", [], Granted: false);

        AbacPageFilter.Filter(pages, scope).Should().BeEmpty();
    }

    // ---- FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 段 3（WikiService の分岐対応） --------
    //
    // 🔴 認可の変更なので、否定形（見えてはならないページが見えない）と陽性対照を対で置く。
    // 「常に false を返す実装」は否定形だけを通す——対の陽性対照が証拠の残り半分である。

    // 認可サービスが実際に返す形: 個人資料ポリシー（owner を主体へ束縛済み）と
    // 組織文書ポリシーが同時にマッチした 2 分岐。
    private static AccessScopeResponse TwoBranchScope(string userId = "me") =>
        new(userId, [], Granted: true, Branches:
        [
            new AccessScopeBranch("個人資料", [new AttributeFilter("owner", [userId])]),
            new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
        ]);

    // #989 回帰テスト 1: 個人資料ポリシーと組織文書ポリシーが同時にマッチしたとき、
    // **両方の集合が見える**（従来はキー単位 union の連言に潰れ、積集合しか見えなかった）。
    [Fact]
    public void Matches_BothBranches_AreVisible_WhenTwoPoliciesMatch()
    {
        var minePersonal = Page(new() { ["owner"] = "me" });                  // 所有者ベースのみで可視
        var orgInternal = Page(new() { ["confidentiality"] = "internal" });   // 属性ベースのみで可視
        var scope = TwoBranchScope();

        AbacPageFilter.Matches(minePersonal, scope).Should().BeTrue(
            "所有者ベースの分岐が効かなければ個人資料が全滅する（本欠陥の形）");
        AbacPageFilter.Matches(orgInternal, scope).Should().BeTrue(
            "属性ベースの分岐が効かなければ組織文書が全滅する（本欠陥の形）");
    }

    // #989 回帰テスト 2（陽性対照）: owner を持たない既存文書（実データ 2,368 件がこの形）が、
    // 組織文書ポリシーの分岐だけで見える。属性欠落は分岐内では不一致だが、別の分岐が救う。
    [Fact]
    public void Matches_DocumentWithoutOwner_IsVisibleViaOrganizationBranch()
    {
        var legacy = Page(new() { ["confidentiality"] = "internal" }); // owner キーそのものが無い
        var scope = TwoBranchScope();

        AbacPageFilter.Matches(legacy, scope).Should().BeTrue(
            "owner 欠落で全滅するなら、選言が連言に潰れている");
    }

    // #989 回帰テスト 3（否定形）: 他人の個人資料は見えない。
    // owner が他人で、組織文書の条件（confidentiality）も満たさないページはどの分岐も満たさない。
    [Fact]
    public void Matches_SomeoneElsesPersonalNote_IsNotVisible()
    {
        var theirs = Page(new() { ["owner"] = "someone-else" });
        var scope = TwoBranchScope(userId: "me");

        AbacPageFilter.Matches(theirs, scope).Should().BeFalse(
            "他人の個人資料が見えるなら分岐評価が緩んでいる——情報が漏れる向きの欠陥");
    }

    // #989 回帰テスト 4（3 と対の陽性対照）: 自分の個人資料は見える。
    [Fact]
    public void Matches_OwnPersonalNote_IsVisible()
    {
        var mine = Page(new() { ["owner"] = "me" });
        var scope = TwoBranchScope(userId: "me");

        AbacPageFilter.Matches(mine, scope).Should().BeTrue(
            "3 と対でなければ「常に false を返す実装」が緑になる");
    }

    // 分岐内は AND: 分岐のフィルタを一部しか満たさないページは、その分岐では不可視。
    [Fact]
    public void Matches_BranchFiltersAreConjunctive()
    {
        var page = Page(new() { ["confidentiality"] = "internal", ["department"] = "legal" });
        var scope = new AccessScopeResponse("u", [], Granted: true, Branches:
        [
            new AccessScopeBranch("部門限定", [
                new AttributeFilter("confidentiality", ["internal"]),
                new AttributeFilter("department", ["sales"]),
            ]),
        ]);

        AbacPageFilter.Matches(page, scope).Should().BeFalse(
            "分岐内が OR に緩むと、ポリシーの連言が壊れて広く見えてしまう");
    }

    // 文書条件の無いポリシーの分岐（フィルタ空）は、そのポリシーの範囲で全件許可
    // （計画の具体判定規則「マッチしたポリシーに文書条件が無い場合は全件許可する」）。
    [Fact]
    public void Matches_BranchWithoutFilters_AllowsAll()
    {
        var page = Page(new() { ["confidentiality"] = "restricted" });
        var scope = new AccessScopeResponse("u", [], Granted: true,
            Branches: [new AccessScopeBranch("無条件", [])]);

        AbacPageFilter.Matches(page, scope).Should().BeTrue();
    }

    // 否定形（deny-by-default は分岐より強い）: Granted=false なら分岐があっても不可視。
    [Fact]
    public void Matches_NotGranted_OverridesBranches()
    {
        var page = Page(new() { ["owner"] = "me" });
        var scope = new AccessScopeResponse("me", [], Granted: false,
            Branches: [new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["me"])])]);

        AbacPageFilter.Matches(page, scope).Should().BeFalse(
            "Granted を読まずに分岐だけ見ると deny-by-default が反転する");
    }

    // 後方互換（回帰）: 分岐が無い応答（未移行の認可サービス・段 1 以前の形）は
    // 従来どおり AllowedFilters で評価される。
    [Fact]
    public void Matches_WithoutBranches_FallsBackToAllowedFilters()
    {
        var page = Page(new() { ["confidentiality"] = "internal" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["internal"])], Granted: true, Branches: []);

        AbacPageFilter.Matches(page, scope).Should().BeTrue(
            "分岐が空のときに AllowedFilters を読まないと、未移行応答で全滅する");
    }

    // IADR-0253 決定 3（否定形）: 述語はプレースホルダを解釈しない。束縛前の文字列が
    // 分岐に紛れても素の文字列比較で不一致になる（解釈すると認可の判断が 2 箇所へ散る）。
    [Fact]
    public void Matches_DoesNotInterpretPlaceholders_InBranches()
    {
        var page = Page(new() { ["owner"] = "me" });
        var scope = new AccessScopeResponse("me", [], Granted: true,
            Branches: [new AccessScopeBranch("個人資料",
                [new AttributeFilter("owner", ["${current_user}"])])]);

        AbacPageFilter.Matches(page, scope).Should().BeFalse(
            "束縛は認可サービスの責務——ここで解釈すると判断が 2 箇所へ散る");
    }
}
