using AuthorizationService.Tests.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests.Features.Authz.OwnerReadPolicyGuard;

// NFR-09, 計画 ADR-0036, ADR-0086, [[IADR-0523]] (#1846 / planning#770): east-west の gRPC の面も、
// トークンの audience（platform-api）を検証する。**呼び出し側サービスのロール（platform-service）を持っていても**、
// platform の API 宛てに発行されていないトークン（aud が無い・MCP クライアント・運用ツール）は Unauthenticated。
// 口は内容の ABAC の門が問う `AuthzScope/GetOwnerReadPolicyStatus`（`ServiceCaller`）を代表に使う。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcAudienceTests
{
    private const string ServiceSubject = "service-account-document-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcAudienceTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private async Task AskAsync(string? audience)
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole], audience);
        await new AuthzScope.AuthzScopeClient(GrpcChannel.ForAddress(_factory.GrpcAddress)).GetOwnerReadPolicyStatusAsync(
            new GetOwnerReadPolicyStatusRequest(),
            headers: new Metadata { { "Authorization", $"Bearer {token}" } },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    // 陽性対照: 共有の audience（platform-api）を持つサービスのトークンは通る。
    [Fact]
    public async Task Platform_apiのaudienceを持つサービスのトークンは通る()
    {
        var act = () => AskAsync(AuthExtensions.DefaultAudience);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mcp-server")]
    [InlineData("grafana")]
    [InlineData("document-service")]
    public async Task Platform_apiのaudienceが無ければサービスのロールを持っていてもUnauthenticated(string? audience)
    {
        var act = () => AskAsync(audience);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
