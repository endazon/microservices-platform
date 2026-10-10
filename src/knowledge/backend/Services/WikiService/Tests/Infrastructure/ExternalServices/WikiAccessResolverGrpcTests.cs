using System.Security.Claims;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using WikiService.Infrastructure.ExternalServices;

namespace WikiService.Tests.Infrastructure.ExternalServices;

// FR-13, FR-05, UC-07, NFR-09, NFR-16, ADR-0004, ADR-0011, ADR-0029, ADR-0075,
// [[IADR-0009]], [[IADR-0335]], [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **T-P2-03** —— ABAC スコープ解決の gRPC 経路が呼び出し先の答えを保って返し、
// （［2026-10-10 / #1255］[[IADR-0533]] 決定 1: 旧形は「REST と gRPC が同じ答え」だった。REST 経路と、その縮退の WARN を固定していた
// 試験クラスは撤去した。gRPC の縮退の WARN は `AuthzScopeGrpcClient` が出す）
// 🔴 **未認証の要求では gRPC を 1 度も呼ばない**ことを固定する。
//
// UC-07 の事前条件は「認証済み」であり、未認証は**認可サービスへ問い合わせずに**拒否する
// （[[IADR-0335]] / #1126）。この短絡は輸送の手前にある —— 輸送を替えたときに短絡の後ろへ
// 滑り込むと、「未認証時の応答がポリシーの内容次第で変わる」#1126 の欠陥が gRPC 経路で再発する。
// **呼び出し回数 0 でしか表明できない。**
[Trait("TestKind", "Unit")]
public class WikiAccessResolverGrpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpContext Authenticated() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "alice"),
            new Claim("clearance", "internal"),
            new Claim("department", "engineering"),
        ], "Test")),
    };

    // 認証されていない主体（`ClaimsIdentity` に認証方式を与えない ＝ IsAuthenticated == false）。
    private static HttpContext Anonymous() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity()),
    };

    private static AccessScopeResponse Granted() => new(
        "alice",
        [new AttributeFilter("confidentiality", ["internal", "public"])],
        true,
        [new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal", "public"])])]);

    // T-P2-03（陽性）: 認証済みなら呼び出し先の答えをそのまま返す。
    [Fact]
    public async Task Grpc_resolves_the_scope_the_authorization_service_returned()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());
        var grpc = await new WikiAccessResolver(fake.Wrap())
            .ResolveAsync(Authenticated(), Ct);

        grpc.Should().BeEquivalentTo(Granted());
        fake.CallCount.Should().Be(1, "陽性対照: 認証済みでは実際に gRPC を通っている");
        fake.LastRequest!.Action.Should().Be("read");
    }

    // 🔴 T-P2-03（本体）: **未認証は gRPC を呼ばない。**
    // 陽性対照は上の 1 本（認証済みなら 1 回呼ぶ）—— 対にしないと
    // 「偽物が壊れていて常に 0 回」と区別できない。
    [Fact]
    public async Task Unauthenticated_request_never_calls_grpc()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        var scope = await new WikiAccessResolver(fake.Wrap())
            .ResolveAsync(Anonymous(), Ct);

        fake.CallCount.Should().Be(0, "UC-07 の事前条件により、未認証は後段へ問い合わせない");
        scope.Granted.Should().BeFalse();
        scope.UserId.Should().Be("anonymous");
    }

    // 縮退: gRPC の輸送失敗はすべて deny-by-default（宛先が未構成のときの UNAVAILABLE を含む）。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task Grpc_failure_degrades_to_deny(StatusCode status)
    {
        var grpcDeny = await new WikiAccessResolver(FakeAuthzScopeClient.Failing(status).Wrap())
            .ResolveAsync(Authenticated(), Ct);

        grpcDeny.Granted.Should().BeFalse();
        grpcDeny.AllowedFilters.Should().BeEmpty();
    }
}
