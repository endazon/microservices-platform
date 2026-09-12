using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Domain;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034, IADR-0242 決定 3:
// **AbacNodeFilter の意味論が WikiService の AbacPageFilter と一致することを固定する。**
//
// 一致していないと、同じ文書が Wiki では見えないのにグラフでは見える（またはその逆）という
// 食い違いが生まれる。ケース群は AbacPageFilterTests と同型に並べてある。
[Trait("TestKind", "Unit")]
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

    // ══ FR-19, ADR-0080 決定 2, [[IADR-0448]] (#1448): 集合値属性（`shared_with` / `tags`）の交差 ══
    //
    // 従前は属性値を**単一文字列**として `AllowedValues.Contains(v, …)` で比べており、
    // カンマ連結の集合値は **1 件も一致しなかった** —— 同じスコープに対して検索側
    // （Qdrant の `Match.Keywords`）だけが交差で答えており、**面によって認可が違った**。
    //
    // 🔴 **3 面（BFF / Graph / Wiki）は同じ述語（`AttributeFilterMatch`）へ委譲している。**
    // その一致は下の `Matches_SetValued_AgreesWithTheContractPredicate` が直接固定する
    // （`AbacPageFilterTests` にも同型の行がある。BFF は `Platform.Bff.Tests` の HTTP 経路で見る）。
    [Theory]
    // 交差（∩ ≠ ∅）で一致する —— 部分集合ではない。
    [InlineData("a,b", new[] { "b" }, true)]
    [InlineData("a,b", new[] { "a" }, true)]
    [InlineData("a,b", new[] { "b", "z" }, true)]
    // 交わらなければ不一致。
    [InlineData("a,b", new[] { "c" }, false)]
    // 🔴 **空集合は何にも一致しない**（空文字・区切りだけ）。
    [InlineData("", new[] { "a" }, false)]
    [InlineData(",", new[] { "a" }, false)]
    // 単一の要素でも集合として読む（従前の単一値一致と同じ答えになる境界）。
    [InlineData("a", new[] { "a" }, true)]
    [InlineData("a", new[] { "b" }, false)]
    public void Matches_SetValuedAttribute_IsEvaluatedAsIntersection(
        string nodeValue, string[] allowed, bool expected)
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, [.. allowed])], true);

        AbacNodeFilter.Matches(Node((DocumentAttributeEncoding.SharedWithKey, nodeValue)), scope)
            .Should().Be(expected);
    }

    // 🔴 **3 面の意味論一致の固定点**: ノードの述語は契約側の述語（`AttributeFilterMatch`）と
    // **同じ答えを返す**。ここが割れると「Wiki では見えないのにグラフでは見える」が戻る。
    [Theory]
    [InlineData("shared_with", "alice,g-1", new[] { "g-1" })]
    [InlineData("shared_with", "alice,g-1", new[] { "g-2" })]
    [InlineData("shared_with", "", new[] { "alice" })]
    [InlineData("tags", "hr,legal", new[] { "legal" })]
    [InlineData("confidentiality", "internal", new[] { "internal" })]
    [InlineData("confidentiality", "internal", new[] { "public" })]
    public void Matches_SetValued_AgreesWithTheContractPredicate(
        string key, string value, string[] allowed)
    {
        var filters = new List<AttributeFilter> { new(key, [.. allowed]) };
        var node = Node((key, value));

        AbacNodeFilter.Matches(node, new AccessScopeResponse("u", filters, true))
            .Should().Be(AttributeFilterMatch.MatchesAll(node.Attributes, filters));
    }

    // 🔴 陰性対照: **単一値キーはカンマで分割しない。** 一律に分割すると、値にカンマを含む
    // 単一値属性が別の意味に化ける（deny 側だが静かに壊れる）。
    [Fact]
    public void Matches_SingleValuedAttribute_IsNotSplitOnComma()
    {
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["hr"])], true);

        AbacNodeFilter.Matches(Node(("department", "hr,sales")), scope).Should().BeFalse(
            "`department` は集合値キーではない（`DocumentAttributeEncoding.SetValuedKeys` に無い）");
    }

    // FR-19, ADR-0061 決定 5・6, [[IADR-0396]] 決定 7: 集合値になっても
    // **個人資料を許可してよいのは裁量（`owner` / `shared_with`）の分岐だけ**である。
    [Fact]
    public void Matches_SharedWithBranch_GrantsAPrivateNote_ButAStaticBranchDoesNot()
    {
        var note = Node(
            ("doc_scope", "private-note"),
            ("owner", "someone-else"),
            ("confidentiality", "restricted"),
            (DocumentAttributeEncoding.SharedWithKey, "alice,g-1"));

        AbacNodeFilter.Matches(note, Branched(new AccessScopeBranch("共有先ベース",
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-1"])])))
            .Should().BeTrue("共有先ベースの分岐は裁量である（ADR-0036 D-06）");

        AbacNodeFilter.Matches(note, Branched(new AccessScopeBranch("静的属性ベース",
            [new AttributeFilter("confidentiality", ["restricted"])])))
            .Should().BeFalse("静的分岐は個人資料を許可しない（ADR-0061 決定 6）");
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
