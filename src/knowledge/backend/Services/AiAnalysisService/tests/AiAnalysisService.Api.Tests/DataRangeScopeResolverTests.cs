using AiAnalysisService.Api.Foundation.Services;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-07, FR-05, UC-02: データ範囲×ABAC スコープ交差の安全性（narrowing-only）を検証する。
// 中核不変条件: 実効スコープは ABAC 許可スコープを決して広げない。
public class DataRangeScopeResolverTests
{
    private static AccessScopeResponse Abac(bool granted, params AttributeFilter[] filters)
        => new("u1", filters.ToList(), granted);

    [Fact]
    public void DenyByDefault_WhenAbacNotGranted_ReturnsNoAccess_EvenWithRange()
    {
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["department"] = ["sales"] });

        var scope = DataRangeScopeResolver.Resolve(Abac(granted: false), range);

        scope.GrantsAccess.Should().BeFalse();
        scope.Filters.Should().BeEmpty();
    }

    [Fact]
    public void NoRange_PreservesAbacFiltersUnchanged()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales", "hr"]));

        var scope = DataRangeScopeResolver.Resolve(abac, range: null);

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo("sales", "hr");
    }

    [Fact]
    public void Intersects_RangeWithAbac_OnSharedKey()
    {
        // ABAC は sales/hr を許可。範囲は sales/finance を要求 → 実効は積集合 {sales} のみ。
        var abac = Abac(true, new AttributeFilter("department", ["sales", "hr"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["department"] = ["sales", "finance"] });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeTrue();
        var f = scope.Filters.Should().ContainSingle().Subject;
        f.Key.Should().Be("department");
        f.AllowedValues.Should().BeEquivalentTo("sales"); // hr/finance は出ない（広げない）
    }

    [Fact]
    public void RangeOutsideAbac_ProducesEmptyIntersection_Denies()
    {
        // 範囲が権限外（finance のみ）を指す → 安全側に倒して全体 deny。
        var abac = Abac(true, new AttributeFilter("department", ["sales"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["department"] = ["finance"] });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeFalse();
        scope.Filters.Should().BeEmpty();
    }

    [Fact]
    public void MultipleKeys_OneEmptyIntersection_DeniesAll()
    {
        // 複数キーのうち 1 つでも積が空（権限外）なら、全体を deny する（漏えい防止の中核不変条件）。
        var abac = Abac(true,
            new AttributeFilter("department", ["sales"]),
            new AttributeFilter("year", ["2025"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new()
            {
                ["department"] = ["sales"],   // 積 OK
                ["year"] = ["finance"],       // 権限外 → 空交差
            });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeFalse();
        scope.Filters.Should().BeEmpty();
    }

    [Fact]
    public void RangeOnlyKey_NotConstrainedByAbac_IsAddedAsNarrowing()
    {
        // ABAC は department のみ制約。範囲が year を追加 → narrowing として安全に追加。
        var abac = Abac(true, new AttributeFilter("department", ["sales"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["year"] = ["2025"] });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().HaveCount(2);
        scope.Filters.Should().ContainSingle(f => f.Key == "department")
            .Which.AllowedValues.Should().BeEquivalentTo("sales");
        scope.Filters.Should().ContainSingle(f => f.Key == "year")
            .Which.AllowedValues.Should().BeEquivalentTo("2025");
    }

    [Fact]
    public void EmptyRangeValues_AreIgnored_LikeNoConstraint()
    {
        var abac = Abac(true, new AttributeFilter("department", ["sales"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["department"] = [] });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().BeEquivalentTo("sales");
    }

    [Fact]
    public void Intersection_IsCaseInsensitive_OnValues()
    {
        var abac = Abac(true, new AttributeFilter("department", ["Sales"]));
        var range = new AnalysisDataRange(
            AttributeFilters: new() { ["department"] = ["sales"] });

        var scope = DataRangeScopeResolver.Resolve(abac, range);

        scope.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.AllowedValues.Should().ContainSingle();
    }

    // ══ FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: 分岐（read の選言）への交差 ══
    //
    // 1 分岐 = 1 ポリシーの文書条件であり、選言の各項は独立した許可根拠である。
    // したがって範囲は**分岐ごとに独立して**交差させ、**全分岐が消えたときだけ**全体 deny にする。

    private static AccessScopeResponse AbacWithBranches(params AccessScopeBranch[] branches)
        => new("u1", UnionOf(branches), true, [.. branches]);

    // 評価器が作る従来面（AllowedFilters = 分岐のキー単位 union）を再現する。
    private static List<AttributeFilter> UnionOf(AccessScopeBranch[] branches)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in branches)
            foreach (var f in b.Filters)
            {
                if (!byKey.TryGetValue(f.Key, out var l)) byKey[f.Key] = l = [];
                foreach (var v in f.AllowedValues)
                    if (!l.Contains(v, StringComparer.OrdinalIgnoreCase)) l.Add(v);
            }
        return [.. byKey.Select(kv => new AttributeFilter(kv.Key, kv.Value))];
    }

    // IADR-0253 決定 2 の反例そのもの。
    private static readonly AccessScopeBranch PolicyA = new("A: 人事の内部資料",
        [new AttributeFilter("confidentiality", ["internal"]), new AttributeFilter("department", ["hr"])]);

    private static readonly AccessScopeBranch PolicyB = new("B: 営業の公開資料",
        [new AttributeFilter("confidentiality", ["public"]), new AttributeFilter("department", ["sales"])]);

    // 範囲指定が無ければ分岐はそのまま運ばれる（後段が選言で判定できる）。
    [Fact]
    public void Branches_WithoutRange_ArePassedThrough()
    {
        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), (AnalysisDataRange?)null);

        scope.GrantsAccess.Should().BeTrue();
        scope.Branches.Should().HaveCount(2);
        scope.Branches!.Select(b => b.Name).Should().BeEquivalentTo(["A: 人事の内部資料", "B: 営業の公開資料"]);
    }

    // 🔴 陽性対照 ＋ 名前の対応: 片方の分岐だけが範囲に合致したら、**その分岐だけ**が残る。
    // 名前を添字で引く実装は、捨てた分岐がある時点でずれる——ここで固定する。
    [Fact]
    public void Branches_RangeMatchingOnlyOneBranch_KeepsThatBranchWithItsOwnName()
    {
        var range = new AnalysisDataRange(AttributeFilters: new() { ["department"] = ["sales"] });

        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), range);

        scope.GrantsAccess.Should().BeTrue();
        scope.Branches.Should().ContainSingle();
        scope.Branches![0].Name.Should().Be("B: 営業の公開資料",
            "生き残った分岐には自分の名前が付く（添字で引くと A の名前が付いてしまう）");
        scope.Branches[0].Filters.Should().ContainSingle(f => f.Key == "department")
            .Which.AllowedValues.Should().BeEquivalentTo(["sales"]);
    }

    // 🔴 負例: 積が空の分岐は**その分岐だけ**が捨てられ、全体 deny にはならない。
    [Fact]
    public void Branches_EmptyIntersectionDropsOnlyThatBranch_NotTheWholeScope()
    {
        var range = new AnalysisDataRange(AttributeFilters: new() { ["confidentiality"] = ["internal"] });

        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), range);

        scope.GrantsAccess.Should().BeTrue("A が生きている以上、全体 deny にしてはならない");
        scope.Branches.Should().ContainSingle().Which.Name.Should().Be("A: 人事の内部資料");
    }

    // 全分岐が消えたときだけ全体 deny（漏えい防止）。
    [Fact]
    public void Branches_WhenAllBranchesDrop_DeniesEverything()
    {
        var range = new AnalysisDataRange(AttributeFilters: new() { ["department"] = ["legal"] });

        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), range);

        scope.GrantsAccess.Should().BeFalse("どの許可根拠でも範囲の外なら何も開放しない");
        scope.Filters.Should().BeEmpty();
        scope.Branches.Should().BeNullOrEmpty();
    }

    // 🔴 混成の拒否: キー単位 union へ畳んでいないこと。
    // 畳むと confidentiality ∈ {internal,public} AND department ∈ {hr,sales} になり、
    // どちらのポリシー単独も許可しない (internal, sales) が通ってしまう。
    [Fact]
    public void Branches_AreNotFoldedIntoKeywiseUnion_CrossPolicyMixtureStaysDenied()
    {
        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), (AnalysisDataRange?)null);

        var mixture = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["department"] = "sales"
        };

        scope.Branches!.Any(b => b.Filters.All(f =>
            mixture.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase)))
            .Should().BeFalse(
                "どの分岐も混成を許可しない（IADR-0253 決定 2 の反例）。"
                + "ここが true になったら分岐がキー単位 union へ潰れている");
    }

    // 分岐が制約しないキーを範囲が指定 → その分岐へ追加（安全な narrowing）。
    [Fact]
    public void Branches_RangeKeyNotConstrainedByBranch_IsAddedAsNarrowing()
    {
        var onlyConfidentiality = new AccessScopeBranch("C: 内部資料",
            [new AttributeFilter("confidentiality", ["internal"])]);
        var range = new AnalysisDataRange(AttributeFilters: new() { ["department"] = ["hr"] });

        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(onlyConfidentiality), range);

        scope.GrantsAccess.Should().BeTrue();
        scope.Branches!.Single().Filters.Should().ContainSingle(f => f.Key == "department")
            .Which.AllowedValues.Should().BeEquivalentTo(["hr"], "無制約だったキーは範囲で絞れる");
    }

    // 従来面（Filters）は生き残った分岐のキー単位 union で作り直される（narrowing-only を保つ）。
    [Fact]
    public void Branches_LegacyFiltersFace_IsRebuiltFromSurvivingBranches()
    {
        var range = new AnalysisDataRange(AttributeFilters: new() { ["department"] = ["sales"] });

        var scope = DataRangeScopeResolver.Resolve(AbacWithBranches(PolicyA, PolicyB), range);

        scope.Filters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().BeEquivalentTo(["public"],
                "A が消えた以上、従来面からも internal は消える（広がらない）");
    }
}
