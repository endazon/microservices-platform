using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-17, UC-10, ADR-0004, ADR-0032, ADR-0034, IADR-0044（多層防御）,
// IADR-0335 決定 4（#1318 欠陥 A）:
// `GraphAccessResolver` が、**未認証の要求では認可サービスを 1 度も呼ばずに deny へ倒す**こと。
//
// 🔴 **Wiki（IADR-0335）とは前提が違う。** Wiki は `/wiki` 群にも各端点にも
// `RequireAuthorization` を持たず、匿名が実際に到達していた。本サービスの消費者はすべて認証を
// 要求しており、**現在の HTTP 表面から匿名でここへ到達する経路は無い**（`AnonymousReachesNothing`
// が 401 でそれを固定する）。本短絡が塞ぐのは「今漏れている穴」ではなく、**fail-closed が端点
// ごとの `RequireAuthorization()` 宣言に依存している**こと自体である。
//
// 🔴 **「呼ばない」は回数でしか表明できない。** `FakeAuthzScopeClient.CallCount` で測る。
// ［2026-10-10 / #1255］[[IADR-0533]] 決定 1: REST 経路を撤去したので、旧形の「両輸送で測る」は gRPC だけになった。
[Trait("TestKind", "Unit")]
public class GraphAnonymousAccessContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 未認証: 認証済みでない `ClaimsIdentity`（＝ `AuthenticationType` が null）。
    // これが本番で匿名要求が持つ姿である（`ClaimsPrincipal` 自体は非 null）。
    private static HttpContext AnonymousCtx() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity()),
    };

    // 🔴 **`User` に何も入っていない場合**（`DefaultHttpContext` の素の姿）も匿名である。
    private static HttpContext BareCtx() => new DefaultHttpContext();

    // 🔴 **身元を 1 つも持たない主体では `ClaimsPrincipal.Identity` が null になる。**
    // 判定を `!= true` ではなく `== false` と綴ると、null は「匿名ではない」側へ落ち、
    // **短絡をすり抜けて認可サービスへ `anonymous` が渡る**。この入力がその綴りの違いを分ける。
    private static HttpContext NullIdentityCtx() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(),
    };

    private static HttpContext AuthenticatedCtx() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("clearance", "internal"),
        ], "TestScheme")),
    };

    private static AccessScopeResponse GrantedAll() => new("alice", [], true);

    // T-5: 未認証 → 認可サービスを**1 度も呼ばない**（身元 `anonymous` は認可サービスへ渡らない）。
    [Theory]
    [InlineData(GraphAccessAction.Read)]
    [InlineData(GraphAccessAction.Write)]
    public async Task Anonymous_is_denied_with_the_anonymous_identity(string action)
    {
        var fake = FakeAuthzScopeClient.Returning(GrantedAll());

        var scope = await new GraphAccessResolver(fake.Wrap()).ResolveAsync(AnonymousCtx(), action, Ct);

        scope.Granted.Should().BeFalse("未認証は deny-by-default へ倒れる");
        scope.AllowedFilters.Should().BeEmpty();
        scope.UserId.Should().Be("anonymous", "認可サービスへは渡らない身元である");
        fake.CallCount.Should().Be(0);
    }

    // T-5（続き）: `HttpContext.User` が素のままでも同じ。
    [Fact]
    public async Task Bare_context_is_denied_without_calling_authorization()
    {
        var fake = FakeAuthzScopeClient.Returning(GrantedAll());

        var scope = await new GraphAccessResolver(fake.Wrap()).ResolveAsync(BareCtx(), GraphAccessAction.Read, Ct);

        scope.Granted.Should().BeFalse();
        fake.CallCount.Should().Be(0);
    }

    // 🔴 T-5（続き）: **`Identity` が null の主体**でも認可サービスを呼ばずに deny。
    // これは `!= true` と `== false` の**綴りの違いを分ける唯一の入力**である。
    [Fact]
    public async Task Null_identity_is_denied_without_calling_authorization()
    {
        var fake = FakeAuthzScopeClient.Returning(GrantedAll());

        var grpc = await new GraphAccessResolver(fake.Wrap())
            .ResolveAsync(NullIdentityCtx(), GraphAccessAction.Read, Ct);

        grpc.Granted.Should().BeFalse("Identity が null の主体は認証済みではない");
        fake.CallCount.Should().Be(0);
    }

    // T-6: 未認証 → **gRPC 経路も 1 度も呼ばない**（短絡は輸送分岐の手前にある）。
    [Theory]
    [InlineData(GraphAccessAction.Read)]
    [InlineData(GraphAccessAction.Write)]
    public async Task Anonymous_is_denied_without_any_grpc_call(string action)
    {
        var fake = FakeAuthzScopeClient.Returning(GrantedAll());

        var scope = await new GraphAccessResolver(fake.Wrap())
            .ResolveAsync(AnonymousCtx(), action, Ct);

        scope.Granted.Should().BeFalse(
            "認可サービスが全許可を返す構えでも、匿名には許可を出さない");
        fake.CallCount.Should().Be(0, "未認証は gRPC でも 1 度も呼ばない");
    }

    // 🔴 T-7 陽性対照（T-5 / T-6 と対）: **認証済みなら認可サービスが呼ばれ、許可が通る。**
    // これが無いと「常に deny を返す実装」が上のすべてを通してしまう。
    [Fact]
    public async Task Authenticated_reaches_grpc_and_is_granted()
    {
        var fake = FakeAuthzScopeClient.Returning(GrantedAll());

        var scope = await new GraphAccessResolver(fake.Wrap())
            .ResolveAsync(AuthenticatedCtx(), GraphAccessAction.Read, Ct);

        scope.Granted.Should().BeTrue();
        fake.CallCount.Should().Be(1, "認証済みなら認可サービスへ問い合わせる");
    }
}
