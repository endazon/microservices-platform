using AwesomeAssertions;
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
        var scope = new BffAccessScope([], GrantsAccess: false);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = "sales" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ空 かつ GrantsAccess=true は「条件なしで全件許可」。
    [Fact]
    public void Matches_AllowsWhenGrantedAndNoFilters()
    {
        var scope = new BffAccessScope([], GrantsAccess: true);

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
        var scope = new BffAccessScope(
            [new AttributeFilter("department", ["sales", "legal"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = value }, scope)
            .Should().Be(expected);
    }

    // FR-05: 文書に当該属性キーが無ければ不一致（narrowing-only）。
    [Fact]
    public void Matches_DeniesWhenAttributeKeyMissing()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("department", ["sales"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["clearance"] = "secret" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ間は AND（1 つでも外れれば不一致）。
    [Fact]
    public void Matches_RequiresAllFiltersToPass()
    {
        var scope = new BffAccessScope(
            [
                new AttributeFilter("department", ["sales"]),
                new AttributeFilter("clearance", ["secret"]),
            ],
            GrantsAccess: true);

        var docAttrs = new Dictionary<string, string> { ["department"] = "sales", ["clearance"] = "public" };

        BffScopeResolver.Matches(docAttrs, scope).Should().BeFalse();
    }

    // ── #989 段 3（FR-19, ADR-0036, IADR-0253 決定 1）: 名前つき分岐の評価 ────────────
    //
    // 分岐内は AND・分岐間は OR。#989 退行防止の写像: 「個人資料（owner ベース）」と
    // 「組織文書（属性ベース）」の両ポリシーがマッチしたとき、**どちらか一方**を満たす文書が
    // 見える（従来のキー単位 union では積だけが見えた）。

    // 正例: 2 分岐のうち片方（属性ベース）だけを満たす文書が可視。
    [Fact]
    public void Matches_EvaluatesBranchesAsDisjunction()
    {
        var scope = new BffAccessScope(
            // Filters（従来の算出値）は union の連言 —— これでは owner を持たない文書は全滅する。
            [new AttributeFilter("owner", ["u1"]), new AttributeFilter("confidentiality", ["internal"])],
            GrantsAccess: true,
            Branches:
            [
                new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["u1"])]),
                new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
            ]);

        // owner 属性を持たない組織文書 → 分岐「組織文書」で可視（従来評価なら不可視だった）。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope)
            .Should().BeTrue();
        // 自分の個人資料（owner だけ合致）→ 分岐「個人資料」で可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["owner"] = "u1", ["confidentiality"] = "restricted" }, scope)
            .Should().BeTrue();
        // 陰性対照: どの分岐も満たさない（他人の個人資料）→ 不可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["owner"] = "u2", ["confidentiality"] = "restricted" }, scope)
            .Should().BeFalse();
    }

    // 🔴 負例（IADR-0253 決定 2 の反例シナリオ）: **キー単位 union はどのポリシー単独も許可しない
    // 値の混成（A=internal×hr・B=public×sales → internal×sales）を許す。** 分岐評価は
    // これを拒否しなければならない —— union へ潰す実装（再導入）はこのテストが落とす。
    [Fact]
    public void Matches_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion()
    {
        var scope = new BffAccessScope(
            // 従来の算出値（キー単位 union）。これで評価すると internal×sales が通ってしまう。
            [
                new AttributeFilter("confidentiality", ["internal", "public"]),
                new AttributeFilter("department", ["hr", "sales"]),
            ],
            GrantsAccess: true,
            Branches:
            [
                new AccessScopeBranch("A", [
                    new AttributeFilter("confidentiality", ["internal"]),
                    new AttributeFilter("department", ["hr"]),
                ]),
                new AccessScopeBranch("B", [
                    new AttributeFilter("confidentiality", ["public"]),
                    new AttributeFilter("department", ["sales"]),
                ]),
            ]);

        // 混成（A にも B にも単独では許可されない組合せ）→ 不可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "sales" },
            scope).Should().BeFalse();

        // 陽性対照: 各ポリシーが単独で許可する組合せは可視（分岐そのものが効いていることの対）。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "hr" },
            scope).Should().BeTrue();
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "public", ["department"] = "sales" },
            scope).Should().BeTrue();
    }

    // 後方互換: Branches が null／空なら従来どおり Filters（連言）で評価する（段 1・2 と同じ扱い）。
    [Theory]
    [InlineData(true)]   // null
    [InlineData(false)]  // 空リスト
    public void Matches_FallsBackToFiltersWhenBranchesAbsent(bool useNull)
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["internal"])],
            GrantsAccess: true,
            Branches: useNull ? null : []);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeTrue();
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "secret" }, scope).Should().BeFalse();
    }

    // 分岐のフィルタが空 = そのポリシーの範囲で全件許可（AbacPageFilter と同一意味論）。
    [Fact]
    public void Matches_BranchWithNoFilters_GrantsAll()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["secret"])],
            GrantsAccess: true,
            Branches: [new AccessScopeBranch("無条件許可", [])]);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeTrue();
    }

    // deny-by-default は分岐があっても変わらない（Granted=false が最優先）。
    [Fact]
    public void Matches_DeniesWhenNotGranted_EvenWithBranches()
    {
        var scope = new BffAccessScope(
            [],
            GrantsAccess: false,
            Branches: [new AccessScopeBranch("無条件許可", [])]);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeFalse();
    }

    // FR-05, FR-06, IADR-0272 決定 4 (#1010): **ResolveAsync の action に既定値を置かない**ことを
    // リフレクションで固定する。既定値つきの引数は「書かなければ read」を意味し、書き忘れが
    // 認可の緩みとして現れる（#993 / #1010 の欠陥そのもの）。GraphService の
    // GraphTypeGateArchitectureTests と同型の構造テスト。
    [Fact]
    public void ResolveAsync_ActionParameter_HasNoDefaultValue()
    {
        var method = typeof(BffScopeResolver).GetMethod(nameof(BffScopeResolver.ResolveAsync));

        method.Should().NotBeNull();
        var actionParam = method!.GetParameters().SingleOrDefault(p => p.Name == "action");
        actionParam.Should().NotBeNull("action 引数が無ければ #1010 の是正が外れている");
        actionParam!.HasDefaultValue.Should().BeFalse(
            "既定値が復活すると『書き忘れ＝read で解決』が再発する（IADR-0272 決定 4）");
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
