using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-17, FR-05, UC-10, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0034, ADR-0036 D-07, ADR-0075,
// [[IADR-0272]] 決定 4, [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **T-P2-02** —— ABAC スコープ解決の gRPC 経路が、呼び出し先の答え（`Granted` / `AllowedFilters` / `Branches`）を
// そのまま返すことを固定する。
// ［2026-10-10 / #1255］[[IADR-0533]] 決定 1: REST 経路を撤去したので、旧形の「REST と同じ答え」は絶対値の表明へ書き換えた。
// REST にしか無い表明（非 2xx・空本文・利用者の本文の形）と REST の縮退の WARN を固定していた 2 つの試験クラスは撤去した
// （gRPC の縮退の WARN は `AuthzScopeGrpcClient` が出し、共有の試験が固定する）。
//
// 🔴 **`read` と `write` の両方で測る。** `action` は既定値を持たない引数であり
// （[[IADR-0272]] 決定 4）、輸送を替えるときに落とすと**書き込み経路が読み取り権限で通る**。
// 片方の action でしか測らないと、その取り違えが緑のまま通る。
[Trait("TestKind", "Unit")]
public class GraphAccessResolverGrpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpContext Ctx()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim("clearance", "internal"),
                new Claim("department", "sales"),
            ], "Test")),
        };
        return ctx;
    }

    // 分岐つきの現実的なスコープ（キー単位 union ＋ 名前つき分岐）。
    private static AccessScopeResponse Granted() => new(
        "test-user",
        [new AttributeFilter("confidentiality", ["internal", "public"])],
        true,
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal", "public"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("owner", ["test-user"])]),
        ]);

    // T-P2-02: gRPC の答えが `Granted` / `AllowedFilters` / `Branches` まで保たれる（read / write とも）。
    [Theory]
    [InlineData(GraphAccessAction.Read)]
    [InlineData(GraphAccessAction.Write)]
    public async Task Grpc_resolves_the_scope_the_authorization_service_returned(string action)
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());
        var grpc = await new GraphAccessResolver(fake.Wrap()).ResolveAsync(Ctx(), action, Ct);

        grpc.Should().BeEquivalentTo(Granted());
        fake.CallCount.Should().Be(1, "陽性対照: 実際に gRPC を通っている");
    }

    // 🔴 `action` は gRPC の本文へ**そのまま**載る。既定へ丸めない。
    [Theory]
    [InlineData(GraphAccessAction.Read, "read")]
    [InlineData(GraphAccessAction.Write, "write")]
    public async Task Sends_the_requested_action_over_grpc(string action, string expected)
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());

        await new GraphAccessResolver(fake.Wrap())
            .ResolveAsync(Ctx(), action, Ct);

        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.Action.Should().Be(expected);
        fake.LastRequest.UserAttributes.Should().ContainKeys("clearance", "department");
    }

    // 縮退: gRPC の輸送失敗はすべて deny-by-default（条件の無い deny。宛先が未構成のときの UNAVAILABLE を含む）。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    public async Task Grpc_failure_degrades_to_deny(StatusCode status)
    {
        var grpcDeny = await new GraphAccessResolver(FakeAuthzScopeClient.Failing(status).Wrap())
            .ResolveAsync(Ctx(), GraphAccessAction.Read, Ct);

        grpcDeny.Granted.Should().BeFalse();
        grpcDeny.AllowedFilters.Should().BeEmpty();
    }
}
