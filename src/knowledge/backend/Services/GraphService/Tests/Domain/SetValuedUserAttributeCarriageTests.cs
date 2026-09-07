using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using System.Security.Claims;
using GraphService.Domain.Ports;

namespace GraphService.Tests.Domain;

// FR-05, FR-09, ADR-0080 決定 1・2, [[IADR-0411]] (#1323):
// **この経路が集合値の利用者属性（`tags` / `projects`）を判定へ運ぶこと**を固定する。
//
// 🔴 **経路ごとに置く理由**: 従前、同じ抽出論理が 6 か所に複製され、6 つとも集合値を落としていた。
// 抽出は共有点（`BffScopeResolver.ExtractUserAttributes`）へ集約したが、
// **1 経路だけ独自の抽出へ戻す変更**は共有点の試験では捕まらない。**経路の側で固定する。**
[Trait("TestKind", "Unit")]
public class SetValuedUserAttributeCarriageTests
{
    private static HttpContext WithClaims(params Claim[] claims) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
        };

    // 陽性: 多値クレームが線上表現で運ばれる。
    [Fact]
    public void 集合値の利用者属性を判定へ運ぶ()
    {
        var attrs = GraphUserContext.FromHttpContext(WithClaims(
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("clearance", "internal"),
            new Claim("tags", "sales"),
            new Claim("tags", "hr"))).Attributes;

        attrs.Should().ContainKey("tags");
        UserAttributeEncoding.Split(attrs["tags"]).Should().BeEquivalentTo(["sales", "hr"],
            "先頭 1 値へ畳むと 2 つ目のタグを条件に持つポリシーが黙ってマッチしなくなる");
        // 陽性対照: 単値キーも従来どおり運ぶ（「集合値だけ運ぶ」実装を落とす）。
        attrs["clearance"].Should().Be("internal");
    }

    // 陰性対照: クレームが無ければキーを載せない（「常に tags を作る」実装を落とす）。
    [Fact]
    public void 集合値のクレームが無ければキーを載せない()
    {
        var attrs = GraphUserContext.FromHttpContext(WithClaims(
            new Claim(ClaimTypes.Name, "bob"),
            new Claim("clearance", "public"))).Attributes;

        attrs.Should().NotContainKey("tags").And.NotContainKey("projects");
    }
}
