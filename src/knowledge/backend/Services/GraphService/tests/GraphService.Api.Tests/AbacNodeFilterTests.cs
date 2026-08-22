using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034, IADR-0238 決定 3:
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
}
