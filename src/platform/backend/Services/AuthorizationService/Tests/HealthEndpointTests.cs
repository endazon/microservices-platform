using AuthorizationService.Tests.Grpc;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;

namespace AuthorizationService.Tests;

// FR-05, FR-09, UC-05, ADR-0004: 認可サービス エンドポイントテスト
[Trait("TestKind", "Integration")]
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-05: ABAC スコープ解決（［2026-10-15 / #1902］REST の `/authz/scope` は撤去し、gRPC 面だけ）
    [Fact]
    public async Task ResolveScope_ReturnsOk()
    {
        var response = await factory.CreateServiceCallerClient().ResolveScopeAsync(
            new AccessScopeRequest("u1", new Dictionary<string, string> { ["department"] = "engineering" }),
            TestContext.Current.CancellationToken);
        response.Status.Should().Be(global::Grpc.Core.StatusCode.OK);
    }

    // #1902: 撤去した REST の受け口はルート表に無い（戻ると、門の試験の無い口が増える）。
    [Fact]
    public async Task RemovedRestScopeFace_IsNotRouted()
    {
        var response = await factory.CreateServiceCallerClient().PostAsync(
            "/authz/scope", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
