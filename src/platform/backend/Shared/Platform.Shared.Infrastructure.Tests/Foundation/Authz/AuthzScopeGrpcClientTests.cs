using System.Security.Claims;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, NFR-09, ADR-0029, ADR-0075, IADR-0379 (#1201): 認可スコープ解決の gRPC 経路（呼び出し側）を固定する。
//
// - 応答の写し（granted / filters / branches）。
// - deny-by-default への縮退 4 経路: granted=false・RpcException（UNAUTHENTICATED / UNAVAILABLE）・s2s トークン取得失敗。
// - BffScopeResolver は gRPC だけで解決する（［2026-10-10 / [[IADR-0533]]］REST の並走は撤去した）。
// - 登録は宛先の有無に関わらず行う。`Services:AuthorizationServiceGrpc` が無ければ常に `UNAVAILABLE` を返す
//   呼び出し器の上に組まれ、deny-by-default へ落ちる（[[IADR-0533]] 決定 2）。
public class AuthzScopeGrpcClientTests
{
    // 生成クライアントは CallInvoker の上に乗る。応答／例外を差し替える最小の CallInvoker。
    private sealed class StubInvoker(Func<ResolveScopeRequest, ResolveScopeResponse> respond) : CallInvoker
    {
        public int Calls { get; private set; }
        public ResolveScopeRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Calls++;
            LastRequest = (ResolveScopeRequest)(object)request!;
            TResponse response;
            try
            {
                response = (TResponse)(object)respond(LastRequest);
            }
            catch (RpcException ex)
            {
                return new AsyncUnaryCall<TResponse>(
                    Task.FromException<TResponse>(ex), Task.FromResult(new Metadata()),
                    () => ex.Status, () => new Metadata(), () => { });
            }
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }

    private static AuthzScopeGrpcClient ClientOver(StubInvoker invoker) =>
        new(new AuthzScope.AuthzScopeClient(invoker), NullLogger<AuthzScopeGrpcClient>.Instance);

    private static ResolveScopeResponse Granted()
    {
        var resp = new ResolveScopeResponse { UserId = "alice", Granted = true };
        resp.AllowedFilters.Add(new AttributeFilter { Key = "confidentiality", AllowedValues = { "internal", "public" } });
        resp.Branches.Add(new AccessScopeBranch
        {
            Name = "attribute",
            Filters = { new AttributeFilter { Key = "confidentiality", AllowedValues = { "internal", "public" } } },
        });
        return resp;
    }

    [Fact]
    public async Task Granted_response_is_mapped_to_bff_scope_with_branches()
    {
        var invoker = new StubInvoker(_ => Granted());

        var scope = await ClientOver(invoker).ResolveAsync(
            "alice", new Dictionary<string, string> { ["department"] = "engineering" }, "read",
            TestContext.Current.CancellationToken);

        scope.Should().NotBeNull();
        scope!.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().Equal("internal", "public");
        scope.Branches.Should().ContainSingle().Which.Name.Should().Be("attribute");
        invoker.LastRequest!.UserId.Should().Be("alice");
        invoker.LastRequest.Action.Should().Be("read");
        invoker.LastRequest.UserAttributes["department"].Should().Be("engineering");
    }

    [Fact]
    public async Task Not_granted_response_degrades_to_null()
    {
        var invoker = new StubInvoker(_ => new ResolveScopeResponse { UserId = "bob", Granted = false });

        var scope = await ClientOver(invoker).ResolveAsync("bob", new Dictionary<string, string>(), "read",
            TestContext.Current.CancellationToken);

        scope.Should().BeNull();
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Rpc_failures_degrade_to_null_instead_of_throwing(StatusCode code)
    {
        var invoker = new StubInvoker(_ => throw new RpcException(new Status(code, "stub")));

        var scope = await ClientOver(invoker).ResolveAsync("alice", new Dictionary<string, string>(), "read",
            TestContext.Current.CancellationToken);

        scope.Should().BeNull("認可サービス不調・資格情報不備は deny-by-default へ倒す");
    }

    // s2s トークンが取れない（構成不備・IdP 不達）ときも匿名で呼ばず deny へ倒す。
    // CallCredentials は CallInvoker の内側で走るので、ここでは取得側の例外を経路の入口で再現する。
    [Fact]
    public async Task Service_token_failure_degrades_to_null()
    {
        // StubInvoker は InvalidOperationException をそのまま投げる（RpcException だけを包む）ので、
        // クライアント側の catch（InvalidOperationException → null）を通ることを確かめる。
        var invoker = new StubInvoker(_ => throw new InvalidOperationException("s2s トークンの取得に失敗しました"));

        var scope = await ClientOver(invoker).ResolveAsync("alice", new Dictionary<string, string>(), "read",
            TestContext.Current.CancellationToken);

        scope.Should().BeNull();
    }

    // BffScopeResolver は gRPC クライアントが DI に在ればそちらを使う（REST の HttpClient は触らない）。
    [Fact]
    public async Task BffScopeResolver_routes_to_grpc_when_the_client_is_registered()
    {
        var invoker = new StubInvoker(_ => Granted());
        var services = new ServiceCollection();
        services.AddSingleton(ClientOver(invoker));
        using var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice"), new Claim("department", "engineering")], "test"));

        var scope = await BffScopeResolver.ResolveAsync(
            new ThrowingHttpClientFactory(), http, BffScopeAction.Read, TestContext.Current.CancellationToken);

        scope.Should().NotBeNull();
        invoker.Calls.Should().Be(1);
        invoker.LastRequest!.UserAttributes["department"].Should().Be("engineering");
    }

    // ［2026-10-10 / [[IADR-0533]] 決定 2］🔴 **宛先が無くても登録する。** 生成クライアントは常に `UNAVAILABLE` を返し、
    // 解決は deny-by-default（null）へ落ちる。理由（構成キーの名前）は WARN の詳細に載る。
    [Fact]
    public async Task Registration_without_the_grpc_address_resolves_to_deny_over_an_unavailable_destination()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthzScopeGrpcClient(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<AuthzScopeGrpcClient>();
        var scope = await client.ResolveAsync("alice", new Dictionary<string, string>(), "read", TestContext.Current.CancellationToken);
        var raw = await client.TryResolveScopeAsync("alice", new Dictionary<string, string>(), "read", TestContext.Current.CancellationToken);

        scope.Should().BeNull("宛先が無いのは認可サービスへ届かないのと同じ deny である");
        raw.Should().BeNull("「引けなかった」として返す（McpServer はこれを Unavailable へ写す）");
        provider.GetService<global::Grpc.Net.Client.GrpcChannel>().Should().BeNull("宛先が無ければチャネルは張らない");
    }

    // 陽性対照: 宛先の無い呼び出し器は `UNAVAILABLE` を返し、詳細に構成キーの名前を載せる。
    [Fact]
    public async Task Unconfigured_destination_answers_unavailable_naming_the_address_key()
    {
        var client = new AuthzScope.AuthzScopeClient(new UnconfiguredGrpcDestination(AuthzScopeGrpcClient.AddressKey));

        var act = async () => await client.ResolveAsync(new ResolveScopeRequest(), cancellationToken: TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<RpcException>()).Which;
        thrown.StatusCode.Should().Be(StatusCode.Unavailable);
        thrown.Status.Detail.Should().Contain(AuthzScopeGrpcClient.AddressKey);
    }

    [Fact]
    public void Registration_wires_channel_client_and_token_provider_with_the_grpc_address()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthzScopeGrpcClient.AddressKey] = "http://authorization-service:8081",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAuthzScopeGrpcClient(config);
        using var provider = services.BuildServiceProvider();

        provider.GetService<AuthzScopeGrpcClient>().Should().NotBeNull();
        provider.GetService<IServiceTokenProvider>().Should().BeOfType<ClientCredentialsServiceTokenProvider>();
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("REST 経路が呼ばれた");
    }
}
