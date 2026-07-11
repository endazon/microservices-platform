using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// FR-05, IADR-0009, Issue #229: Shared.Infrastructure へ切り出した ABAC スコープ解決ヘルパの純ロジック単体テスト。
// deny-by-default・AND/OR・大文字小文字非依存・claim 抽出を直接検証する（ResolveAsync の HTTP 経路は
// Document/Search の BFF エンドポイントテストが回帰保証する）。
public class BffScopeResolverTests
{
    // FR-05: 許可ポリシー無し（GrantsAccess=false）は deny-by-default で常に不一致。
    [Fact]
    public void Matches_DeniesWhenScopeGrantsNoAccess()
    {
        var scope = new AccessScope([], GrantsAccess: false);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = "sales" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ空 かつ GrantsAccess=true は「条件なしで全件許可」。
    [Fact]
    public void Matches_AllowsWhenGrantedAndNoFilters()
    {
        var scope = new AccessScope([], GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string>(), scope).Should().BeTrue();
    }

    // FR-05: 値集合内は OR（大文字小文字非依存）で一致する。
    [Theory]
    [InlineData("sales", true)]
    [InlineData("SALES", true)]
    [InlineData("legal", true)]
    [InlineData("hr", false)]
    public void Matches_EvaluatesValueSetAsCaseInsensitiveOr(string value, bool expected)
    {
        var scope = new AccessScope(
            [new AttributeFilter("department", ["sales", "legal"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = value }, scope)
            .Should().Be(expected);
    }

    // FR-05: 文書に当該属性キーが無ければ不一致（narrowing-only）。
    [Fact]
    public void Matches_DeniesWhenAttributeKeyMissing()
    {
        var scope = new AccessScope(
            [new AttributeFilter("department", ["sales"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["clearance"] = "secret" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ間は AND（1 つでも外れれば不一致）。
    [Fact]
    public void Matches_RequiresAllFiltersToPass()
    {
        var scope = new AccessScope(
            [
                new AttributeFilter("department", ["sales"]),
                new AttributeFilter("clearance", ["secret"]),
            ],
            GrantsAccess: true);

        var docAttrs = new Dictionary<string, string> { ["department"] = "sales", ["clearance"] = "public" };

        BffScopeResolver.Matches(docAttrs, scope).Should().BeFalse();
    }

    // FR-05: JWT の clearance/department クレームを利用者属性へ写す。
    [Fact]
    public void ExtractUserAttributes_ReadsClearanceAndDepartmentClaims()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("clearance", "secret"),
                new Claim("department", "sales"),
            ], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["clearance"] = "secret",
            ["department"] = "sales",
        });
    }

    // FR-05: 対象クレームが無ければ該当キーを含めない（欠落は付与しない）。
    [Fact]
    public void ExtractUserAttributes_OmitsMissingClaims()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("department", "legal")], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs.Should().ContainKey("department").And.NotContainKey("clearance");
    }
}
