using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Features.McpTools.Declare;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace GraphService.Tests.Features.McpTools.Declare;

// FR-16, NFR-09, NFR-16, ADR-0024 §2, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5,
// [[IADR-0462]]（2026-09-26 追記 / #1515, #1255 経路 ④-a）: ツール定義の自己申告の gRPC 面
// （`platform.mcp.v1.McpToolDeclarations/Declare`）を**本番の Program.cs のまま・実 Kestrel の h2c ポート**で往復し、
// REST `GET /internal/mcp-tools` との同値と、s2s の要求（無し → UNAUTHENTICATED・利用者 → PERMISSION_DENIED）を固定する。
//
// 陽性対照（s2s で申告が返る）と陰性対照（拒否）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
// 待受はループバック（`GrpcKestrelFactory` が 127.0.0.1 の空きポートを選び、起動直後に表明する）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcMcpToolDeclarationTests
{
    private readonly GrpcKestrelFactory _factory;

    public GrpcMcpToolDeclarationTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Pb.McpToolDeclarations.McpToolDeclarationsClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    // 陽性対照 ＋ 同値: s2s トークンを CallCredentials で付けた h2c チャネル（MCP サーバーの収集器と同じ組み方）で往復し、
    // REST と**同じ申告**（6 項目・順序とも）が返る。
    [Fact]
    public async Task Declare_over_h2c_with_service_token_returns_the_same_declarations_as_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress,
            new FixedTokenProvider(GrpcKestrelFactory.IssueToken("service-account-mcp-server", [PlatformAuthPolicies.ServiceRole])));
        var grpc = await new Pb.McpToolDeclarations.McpToolDeclarationsClient(channel)
            .DeclareAsync(new Pb.DeclareMcpToolsRequest(), cancellationToken: ct);

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var rest = (await http.GetFromJsonAsync<ServiceToolDeclarations>(McpToolEndpoints.ToolsPath, ct))!;

        grpc.Service.Should().Be("graph-service", "空の service は収集器が申告なしへ落とす");
        grpc.Service.Should().Be(rest.Service);
        grpc.Tools.Select(t => t.Name).Should().Contain(["graph.get_backlinks", "graph.get_links", "graph.traverse"], "★ 陽性対照 —— 両方が空で一致したのではない");
        grpc.Tools.Select(t => (t.Name, t.Description, t.InputSchema, t.Endpoint, t.RequiredScope, t.EgressClass))
            .Should().Equal(rest.Tools.Select(t => (t.Name, t.Description, t.InputSchema, t.Endpoint, t.RequiredScope, t.EgressClass)),
                "輸送を替えても申告の中身も順序も変わらない（同じ McpToolDeclarationSource.Declare を通る）");
    }

    // 陰性対照: 資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Declare_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().DeclareAsync(
            new Pb.DeclareMcpToolsRequest(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 利用者トークンの転送を機械で止める点。**管理者の利用者トークンでも PERMISSION_DENIED**
    // （REST の受け口は認証を持たないので、「認証さえあれば通る」形にすると s2s の面が利用者トークンで開く）。
    [Fact]
    public async Task Declare_with_forwarded_admin_user_token_is_permission_denied()
    {
        var act = async () => await PlainClient().DeclareAsync(
            new Pb.DeclareMcpToolsRequest(),
            headers: Bearer(GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole])),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 構造の門: gRPC サービス型が ServiceCaller を宣言している（外れると上の 2 件が落ちるが、どの層で外れたかを名指しする）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(McpToolDeclarationGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // s2s トークンの発行側を固定値へ差し替える（IdP を持たないため）。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }
}
