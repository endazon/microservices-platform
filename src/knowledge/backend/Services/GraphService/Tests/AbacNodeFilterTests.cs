using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034, IADR-0242 決定 3:
// **AbacNodeFilter の意味論が WikiService の AbacPageFilter と一致することを固定する。**
//
// 一致していないと、同じ文書が Wiki では見えないのにグラフでは見える（またはその逆）という
// 食い違いが生まれる。ケース群は AbacPageFilterTests と同型に並べてある。
public class AbacNodeFilterTests
{
    private static GraphDocument Node(params (string Key, string Value)[] attrs)
        => GraphDocument.Create(
            Guid.NewGuid(), "t",
            attrs.ToDictionary(a => a.Key, a => a.Value),
            null, DateTimeOffset.UtcNow);

    // FR-05: deny-by-default。マッチするポリシーが無ければ何も可視でない。
    [Fact]
    public void Denies_everything_when_not_granted()
    {
        var scope = new AccessScopeResponse("u", [], false);

        AbacNodeFilter.Matches(Node(("confidentiality", "public")), scope).Should().BeFalse();
    }

    // フィルタが空 かつ Granted=true → 条件無しで全件可。
    [Fact]
    public void Allows_all_when_granted_without_filters()
    {
        var scope = new AccessScopeResponse("u", [], true);

        AbacNodeFilter.Matches(Node(), scope).Should().BeTrue();
        AbacNodeFilter.Matches(Node(("confidentiality", "restricted")), scope).Should().BeTrue();
    }

    // 値集合内は OR。
    [Fact]
    public void Values_within_a_filter_are_or()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["public", "internal"])], true);

        AbacNodeFilter.Matches(Node(("confidentiality", "public")), scope).Should().BeTrue();
        AbacNodeFilter.Matches(Node(("confidentiality", "internal")), scope).Should().BeTrue();
        AbacNodeFilter.Matches(Node(("confidentiality", "confidential")), scope).Should().BeFalse();
    }

    // フィルタ間は AND。
    [Fact]
    public void Filters_across_keys_are_and()
    {
        var scope = new AccessScopeResponse("u",
        [
            new AttributeFilter("confidentiality", ["internal"]),
            new AttributeFilter("department", ["sales"]),
        ], true);

        AbacNodeFilter.Matches(
            Node(("confidentiality", "internal"), ("department", "sales")), scope).Should().BeTrue();
        AbacNodeFilter.Matches(
            Node(("confidentiality", "internal"), ("department", "hr")), scope).Should().BeFalse();
    }

    // 🔴 **属性キーを持たないノードは不一致**（欠落は安全側に倒す）。
    // ここが逆向きだと、属性の複製がまだ届いていない文書が全部見えることになる。
    [Fact]
    public void Node_missing_the_attribute_key_does_not_match()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["internal"])], true);

        AbacNodeFilter.Matches(Node(("department", "sales")), scope).Should().BeFalse();
        AbacNodeFilter.Matches(Node(), scope).Should().BeFalse();
    }

    // 比較は大文字小文字を無視する（AbacPageFilter と同じ）。
    [Fact]
    public void Comparison_is_case_insensitive()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["Internal"])], true);

        AbacNodeFilter.Matches(Node(("confidentiality", "internal")), scope).Should().BeTrue();
    }

    // ADR-0034 決定 1: AuthorizedNode は述語と同じ判定で作られる（構築経路の意味論一致）。
    [Fact]
    public void Authorize_returns_null_exactly_when_predicate_denies()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["internal"])], true);

        var allowed = Node(("confidentiality", "internal"));
        var denied = Node(("confidentiality", "restricted"));

        AuthorizedNode.Authorize(allowed, scope).Should().NotBeNull();
        AuthorizedNode.Authorize(denied, scope).Should().BeNull();
    }

    // ADR-0034 決定 2: まとめて判定しても非許可は「黙って落ちる」（件数にも出さない）。
    [Fact]
    public void AuthorizeAll_drops_denied_nodes_silently()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["internal"])], true);

        var result = AuthorizedNode.AuthorizeAll(
        [
            Node(("confidentiality", "internal")),
            Node(("confidentiality", "restricted")),
            Node(("confidentiality", "internal")),
        ], scope);

        result.Should().HaveCount(2);
    }

    // ══ FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: 認可スコープの分岐（read の選言）══
    //
    // 分岐内 AND・分岐間 OR。**AbacPageFilter（WikiService）・BffScopeResolver.Matches・
    // 検索側 ScopeFilter と同一の意味論**である（ずれると経路によって認可が変わる）。

    private static readonly AccessScopeBranch PolicyA = new("A: 人事の内部資料",
        [new AttributeFilter("confidentiality", ["internal"]), new AttributeFilter("department", ["hr"])]);

    private static readonly AccessScopeBranch PolicyB = new("B: 営業の公開資料",
        [new AttributeFilter("confidentiality", ["public"]), new AttributeFilter("department", ["sales"])]);

    private static AccessScopeResponse Branched(params AccessScopeBranch[] branches)
        => new("u", [], true, [.. branches]);

    // 正例: 分岐間 OR —— A だけを満たすノードと B だけを満たすノードが両方可視。
    [Fact]
    public void Matches_EvaluatesBranchesAsDisjunction()
    {
        var scope = Branched(PolicyA, PolicyB);

        AbacNodeFilter.Matches(Node(("confidentiality", "internal"), ("department", "hr")), scope)
            .Should().BeTrue("分岐 A を満たす");
        AbacNodeFilter.Matches(Node(("confidentiality", "public"), ("department", "sales")), scope)
            .Should().BeTrue("分岐 B を満たす");
    }

    // 🔴 負例: 混成の拒否（キー単位 union への退行を捕まえる）。
    [Fact]
    public void Matches_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion()
    {
        AbacNodeFilter.Matches(
            Node(("confidentiality", "internal"), ("department", "sales")),
            Branched(PolicyA, PolicyB))
            .Should().BeFalse(
                "(internal, sales) はどちらのポリシー単独でも許可されない（IADR-0253 決定 2 の反例）");
    }

    // 陽性対照: 分岐 1 本だけなら、その分岐を満たすノードだけが可視（「常に true」を落とす）。
    [Fact]
    public void Matches_WithSingleBranch_OnlyThatPolicyGrants()
    {
        var scope = Branched(PolicyA);

        AbacNodeFilter.Matches(Node(("confidentiality", "internal"), ("department", "hr")), scope)
            .Should().BeTrue();
        AbacNodeFilter.Matches(Node(("confidentiality", "public"), ("department", "sales")), scope)
            .Should().BeFalse();
    }

    // 属性キーの欠落は分岐内でも不一致（欠落は安全側へ倒す）。
    [Fact]
    public void Matches_NodeMissingBranchAttribute_IsNotVisible()
    {
        AbacNodeFilter.Matches(Node(("confidentiality", "internal")), Branched(PolicyA))
            .Should().BeFalse("department を持たないノードは分岐 A を満たさない");
    }

    // 分岐のフィルタが空 = そのポリシーの範囲で全件許可（AbacPageFilter と同一意味論）。
    [Fact]
    public void Matches_BranchWithNoFilters_GrantsAll()
    {
        AbacNodeFilter.Matches(
            Node(("confidentiality", "secret")),
            Branched(new AccessScopeBranch("無条件許可", [])))
            .Should().BeTrue();
    }

    // deny-by-default は分岐があっても最優先（Granted=false）。
    [Fact]
    public void Matches_DeniesWhenNotGranted_EvenWithBranches()
    {
        var scope = new AccessScopeResponse("u", [], false,
            [new AccessScopeBranch("無条件許可", [])]);

        AbacNodeFilter.Matches(Node(("confidentiality", "internal")), scope).Should().BeFalse();
    }

    // 後方互換（回帰）: 分岐が無い応答は従来どおり AllowedFilters の連言で評価する。
    [Theory]
    [InlineData(true)]   // Branches = null
    [InlineData(false)]  // Branches = 空
    public void Matches_WithoutBranches_FallsBackToAllowedFilters(bool useNull)
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("confidentiality", ["internal"])], true,
            Branches: useNull ? null : []);

        AbacNodeFilter.Matches(Node(("confidentiality", "internal")), scope).Should().BeTrue(
            "分岐が空のときに AllowedFilters を読まないと、未移行応答で全滅する");
        AbacNodeFilter.Matches(Node(("confidentiality", "secret")), scope).Should().BeFalse();
    }

    // IADR-0253 決定 3: ${current_user} は述語で解釈しない（束縛は認可サービスの責務）。
    [Fact]
    public void Matches_DoesNotInterpretPlaceholders_InsideBranches()
    {
        var scope = Branched(new AccessScopeBranch("個人資料",
            [new AttributeFilter("owner", ["${current_user}"])]));

        AbacNodeFilter.Matches(Node(("owner", "u")), scope).Should().BeFalse(
            "述語がプレースホルダを解釈すると認可の判断が 2 箇所へ散る");
    }

    // ホップごと ABAC の型ゲート（IADR-0242 決定 2）を通しても分岐が効く。
    [Fact]
    public void AuthorizedNode_Authorize_AppliesBranches()
    {
        var scope = Branched(PolicyA, PolicyB);

        AuthorizedNode.Authorize(
            Node(("confidentiality", "internal"), ("department", "hr")), scope).Should().NotBeNull();
        AuthorizedNode.Authorize(
            Node(("confidentiality", "internal"), ("department", "sales")), scope).Should().BeNull(
            "混成はホップ展開の経路でも拒否される");
    }
}
