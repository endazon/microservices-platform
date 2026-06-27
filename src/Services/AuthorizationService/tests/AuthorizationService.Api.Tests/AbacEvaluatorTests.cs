using AuthorizationService.Api.Domain;
using AuthorizationService.Api.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AuthorizationService.Api.Tests;

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
}
