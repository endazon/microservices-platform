using AiAnalysisService.Api.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

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
}
