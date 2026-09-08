using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-03, FR-05, FR-07, FR-19, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0036,
// ADR-0043, ADR-0046 D-06, [[IADR-0012]], [[IADR-0151]], [[IADR-0253]] 決定 1・2,
// [[IADR-0415]] (#1340): narrowing の規則そのものへ掛ける固定。
//
// 🔴 **規則を共有点へ 1 つだけ置いた以上、その 1 つに直接の試験が要る**（PR #1341 のレビュー指摘）。
// 従前は呼び出し元（`DataRangeScopeResolverTests`）と受け口（`ScopeIsNotWidenedByCallerTests`）
// 経由の間接被覆だけだった —— **共有点を直接壊す変異が、どちらの器を通るかに依存して**
// 捕まったり捕まらなかったりする。
//
// 🔴 **不変条件はただ 1 つ: 実効スコープは許可スコープの部分集合である。**
[Trait("TestKind", "Unit")]
public class ScopeNarrowingTests
{
    private static AccessScope Allow(params AttributeFilter[] filters) => new([.. filters], true);

    private static Dictionary<string, List<string>> Ask(string key, params string[] values) =>
        new() { [key] = [.. values] };

    // 🔴 T-01: **指定は許可値集合を広げない。** これが #1340 の本体である。
    [Fact]
    public void A_request_cannot_add_values_that_the_scope_does_not_allow()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales"])), Ask("dept", "hr"));

        // 積が空 ⇒ 全体 deny（当該キーを黙って外して全件にしない）。
        result.GrantsAccess.Should().BeFalse("権限の外を指した指定は全件表示に化けてはならない");
    }

    // 🔴 T-02: **指定は許可の中で絞る。** 広げないことだけを固定すると、
    // 「指定を丸ごと無視する実装」でも緑になる（陽性対照）。
    [Fact]
    public void A_request_narrows_within_what_the_scope_allows()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales", "eng"])), Ask("dept", "sales"));

        result.GrantsAccess.Should().BeTrue();
        result.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo(["sales"], "eng は絞られて消える");
    }

    // 許可が制約していないキーへの指定は、そのまま絞り込みとして足せる（安全な narrowing）。
    [Fact]
    public void A_request_on_an_unconstrained_key_is_added_as_a_narrowing()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales"])), Ask("project", "apollo"));

        result.GrantsAccess.Should().BeTrue();
        result.Filters.Should().Contain(f => f.Key == "project");
        result.Filters.Should().Contain(f => f.Key == "dept");
    }

    // deny-by-default: 許可が無ければ、いかなる指定でも何も開かない。
    [Fact]
    public void A_scope_that_grants_nothing_stays_closed()
        => ScopeNarrowing.Apply(new AccessScope([], false), Ask("dept", "sales"))
            .GrantsAccess.Should().BeFalse();

    // null の許可も同じ（未解決＝閲覧可能なし）。
    [Fact]
    public void A_missing_scope_is_treated_as_deny()
        => ScopeNarrowing.Apply(null, Ask("dept", "sales")).GrantsAccess.Should().BeFalse();

    // 指定が無ければ許可はそのまま通る（絞り込みが無いだけ）。
    [Fact]
    public void No_request_leaves_the_scope_untouched()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales", "eng"])), (IReadOnlyDictionary<string, List<string>>?)null);

        result.GrantsAccess.Should().BeTrue();
        result.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo(["sales", "eng"]);
    }

    // 🔴 T-03: **分岐は分岐ごとに独立して絞る。積が空の分岐だけを捨てる。**
    // 他の分岐が生きていれば、その根拠での閲覧は依然として正当である。
    [Fact]
    public void Each_branch_is_narrowed_on_its_own_and_only_empty_branches_are_dropped()
    {
        var scope = new AccessScope([], true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("dept", ["sales"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("dept", ["eng"])]),
        ]);

        var result = ScopeNarrowing.Apply(scope, Ask("dept", "eng"));

        result.GrantsAccess.Should().BeTrue();
        result.Branches.Should().ContainSingle("sales の分岐だけが範囲の外で落ちる")
            .Which.Name.Should().Be("owner");
    }

    // 🔴 T-04: **全分岐が消えたときだけ全体 deny。**
    [Fact]
    public void All_branches_dropped_means_deny()
    {
        var scope = new AccessScope([], true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("dept", ["sales"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("dept", ["eng"])]),
        ]);

        ScopeNarrowing.Apply(scope, Ask("dept", "hr")).GrantsAccess.Should().BeFalse();
    }

    // 🔴 T-05: **キー単位 union へ畳まない**（[[IADR-0253]] 決定 2 の反例）。
    // A={confidentiality:internal, dept:hr} と B={confidentiality:public, dept:sales} を
    // union すると、**どちらのポリシー単独も許可しない混成 (internal, sales) を許す**。
    // 生き残った分岐が分岐のまま返ることを固定する。
    [Fact]
    public void Branches_are_not_flattened_into_one_conjunction()
    {
        var scope = new AccessScope([], true,
        [
            new AccessScopeBranch("A",
                [new AttributeFilter("confidentiality", ["internal"]), new AttributeFilter("dept", ["hr"])]),
            new AccessScopeBranch("B",
                [new AttributeFilter("confidentiality", ["public"]), new AttributeFilter("dept", ["sales"])]),
        ]);

        var result = ScopeNarrowing.Apply(scope, (IReadOnlyDictionary<string, List<string>>?)null);

        result.Branches.Should().HaveCount(2, "選言は選言のまま運ぶ —— 畳むと混成を許す");
    }

    // 単値の指定（FR-03 後方互換）も同じ規則を通る。**「単値だから素通し」の枝を作らない。**
    [Fact]
    public void A_single_valued_request_goes_through_the_same_rule()
    {
        var widen = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales"])),
            new Dictionary<string, string> { ["dept"] = "hr" });

        widen.GrantsAccess.Should().BeFalse("単値でも許可を広げない");

        var narrow = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales", "eng"])),
            new Dictionary<string, string> { ["dept"] = "sales" });

        narrow.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo(["sales"], "★ 陽性対照 —— 絞り込みは効く");
    }

    // ── 権威 × 主張（`Apply(AccessScope, AccessScope)`。#1339 の受け口が使う面）──
    //
    // 🔴 **呼び出し元の主張は権限の根拠ではない。狭める方向にしか効かない。**

    // 🔴 T-06: **主張は権威を超えられない。**
    [Fact]
    public void A_claim_cannot_exceed_what_the_authority_allows()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales"])),
            new AccessScope([], true));   // 「制約なしで全部見てよい」と主張する

        result.GrantsAccess.Should().BeTrue();
        result.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo(["sales"], "権威の制約が残る");
    }

    // 陽性対照: 主張は絞り込みとしては効く。
    [Fact]
    public void A_claim_still_narrows_within_the_authority()
    {
        var result = ScopeNarrowing.Apply(
            Allow(new AttributeFilter("dept", ["sales", "eng"])),
            new AccessScope([new AttributeFilter("dept", ["sales"])], true));

        result.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo(["sales"]);
    }

    // 主張が無い（未指定・deny）＝絞り込みが無い。**権威をそのまま使う。**
    [Fact]
    public void No_claim_means_no_narrowing()
    {
        var authority = Allow(new AttributeFilter("dept", ["sales"]));

        ScopeNarrowing.Apply(authority, (AccessScope?)null).Should().BeEquivalentTo(authority);
        ScopeNarrowing.Apply(authority, new AccessScope([], false)).Should().BeEquivalentTo(authority);
    }

    // 🔴 T-07: **呼び出し元が落とした分岐は落ちる**（narrowing として正当）。
    [Fact]
    public void A_branch_the_claim_omits_is_dropped()
    {
        var authority = new AccessScope([], true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("dept", ["sales"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("dept", ["eng"])]),
        ]);
        var claim = new AccessScope([], true,
            [new AccessScopeBranch("owner", [new AttributeFilter("dept", ["eng"])])]);

        var result = ScopeNarrowing.Apply(authority, claim);

        result.Branches.Should().ContainSingle("主張しなかった分岐は絞り込みとして落ちる")
            .Which.Name.Should().Be("owner");
    }

    // 🔴 T-08: **主張にしか無い分岐は無視する**（広げられない）。
    [Fact]
    public void A_branch_that_only_the_claim_has_is_ignored()
    {
        var authority = new AccessScope([], true,
            [new AccessScopeBranch("attribute", [new AttributeFilter("dept", ["sales"])])]);
        var claim = new AccessScope([], true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("dept", ["sales"])]),
            new AccessScopeBranch("forged", [new AttributeFilter("dept", ["hr"])]),
        ]);

        var result = ScopeNarrowing.Apply(authority, claim);

        result.Branches.Should().ContainSingle("権威に無い分岐は足せない").Which.Name.Should().Be("attribute");
    }

    // 🔴 T-09: **権威が平坦で主張が分岐なら、分岐のまま残す**（[[IADR-0253]] 決定 2）。
    // ここで主張のキー単位 union へ畳むと、**どちらのポリシー単独も許可しない混成を許す。**
    [Fact]
    public void A_branched_claim_against_a_flat_authority_keeps_its_disjunction()
    {
        var authority = Allow(new AttributeFilter("confidentiality", ["internal", "public"]));
        var claim = new AccessScope([], true,
        [
            new AccessScopeBranch("A",
                [new AttributeFilter("confidentiality", ["internal"]), new AttributeFilter("dept", ["hr"])]),
            new AccessScopeBranch("B",
                [new AttributeFilter("confidentiality", ["public"]), new AttributeFilter("dept", ["sales"])]),
        ]);

        var result = ScopeNarrowing.Apply(authority, claim);

        result.Branches.Should().HaveCount(2, "選言は選言のまま運ぶ —— 畳むと混成 (internal, sales) を許す");
    }
}
