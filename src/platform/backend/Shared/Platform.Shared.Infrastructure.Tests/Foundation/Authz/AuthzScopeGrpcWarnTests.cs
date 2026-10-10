using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Tests.Testing;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, NFR-09, ADR-0004, [[IADR-0379]] 決定 4, [[IADR-0533]] 決定 1・2 (#1378, #1255):
// **スコープ解決が deny-by-default へ縮退した理由を WARN で出す**ことを、gRPC の経路（唯一の輸送）で固定する。
//
// 従前この性質（#1378: 縮退を無言で `Granted=false` へ畳まない）は REST 側の WARN の試験 4 クラスが持っていたが、
// REST 実装の撤去とともに消えた。縮退の WARN はいま `AuthzScopeGrpcClient` が出すので、ここで 3 つの入口
// （BFF の `ResolveAsync`・後段の `ResolveScopeAsync`・McpServer の `TryResolveScopeAsync`）とも表明する。
//
// 🔴 **陽性と陰性を対にする。** 輸送の失敗（宛先の未構成を含む）は WARN を出し、正当な deny（`Granted=false`）と許可は出さない。
// 🔴 **WARN に利用者 ID・属性を載せない**（旧 REST 側の `The_warning_does_not_carry_the_user_or_attributes` の相当）。
[Trait("TestKind", "Unit")]
public class AuthzScopeGrpcWarnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string User = "alice-unique-user";
    private const string AttributeValue = "secret-clearance-value";

    private static readonly Dictionary<string, string> Attributes = new() { ["clearance"] = AttributeValue };

    private static (AuthzScopeGrpcClient Client, RecordingLogger<AuthzScopeGrpcClient> Log) Over(CallInvoker invoker)
    {
        var log = new RecordingLogger<AuthzScopeGrpcClient>();
        return (new AuthzScopeGrpcClient(new Pb.AuthzScope.AuthzScopeClient(invoker), log), log);
    }

    private static (AuthzScopeGrpcClient Client, RecordingLogger<AuthzScopeGrpcClient> Log) Failing(StatusCode status) =>
        Over(new FixedInvoker(failWith: status));

    private static (AuthzScopeGrpcClient Client, RecordingLogger<AuthzScopeGrpcClient> Log) Answering(bool granted) =>
        Over(new FixedInvoker(response: new Pb.ResolveScopeResponse { UserId = User, Granted = granted }));

    // 3 つの入口を同じ表で回す（どれかだけ WARN を落とす変異を捕まえる）。
    public static TheoryData<string> Entrances() => ["bff", "scope", "try"];

    private static async Task<bool> ResolveGrantsAsync(AuthzScopeGrpcClient client, string entrance) => entrance switch
    {
        "bff" => (await client.ResolveAsync(User, Attributes, "read", Ct))?.GrantsAccess ?? false,
        "scope" => (await client.ResolveScopeAsync(User, Attributes, "read", Ct)).Granted,
        _ => (await client.TryResolveScopeAsync(User, Attributes, "read", Ct))?.Granted ?? false,
    };

    // 🔴 陽性: 輸送の失敗は deny へ畳み、WARN を 1 件出す（ステータスを載せる）。
    [Theory]
    [MemberData(nameof(Entrances))]
    public async Task A_transport_failure_degrades_to_deny_with_a_warning(string entrance)
    {
        foreach (var status in new[] { StatusCode.Unavailable, StatusCode.Unauthenticated, StatusCode.PermissionDenied, StatusCode.DeadlineExceeded })
        {
            var (client, log) = Failing(status);

            (await ResolveGrantsAsync(client, entrance)).Should().BeFalse($"{status} は deny-by-default へ畳む");
            var warn = log.OfLevel(LogLevel.Warning).Should().ContainSingle($"{entrance}: {status} の縮退は理由を残す").Subject;
            warn.State.Should().Contain(p => p.Key == "Status" && Equals(p.Value, status));
        }
    }

    // 🔴 陽性（宛先の未構成）: [[IADR-0533]] 決定 2 の UNAVAILABLE も同じ WARN を出し、詳細に構成キーの名前が載る。
    [Theory]
    [MemberData(nameof(Entrances))]
    public async Task An_unconfigured_destination_is_warned_with_the_address_key(string entrance)
    {
        var (client, log) = Over(new UnconfiguredGrpcDestination(AuthzScopeGrpcClient.AddressKey));

        (await ResolveGrantsAsync(client, entrance)).Should().BeFalse();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(AuthzScopeGrpcClient.AddressKey, "設定漏れが WARN から読める");
    }

    // 🔴 WARN に利用者 ID・属性を載せない（整形済みの本文にも、構造化ログの値にも）。
    [Theory]
    [MemberData(nameof(Entrances))]
    public async Task The_warning_does_not_carry_the_user_or_attributes(string entrance)
    {
        var (client, log) = Failing(StatusCode.Unavailable);

        await ResolveGrantsAsync(client, entrance);

        var warn = log.OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Message.Should().NotContain(User).And.NotContain(AttributeValue);
        warn.State.Select(p => p.Value?.ToString()).Should().NotContain([User, AttributeValue]);
    }

    // 🔴 陰性対照: 正当な deny（`Granted=false`）は WARN を出さない（縮退ではない）。
    [Theory]
    [MemberData(nameof(Entrances))]
    public async Task A_legitimate_deny_is_not_logged_as_a_warning(string entrance)
    {
        var (client, log) = Answering(granted: false);

        (await ResolveGrantsAsync(client, entrance)).Should().BeFalse();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 陰性対照: 許可も WARN を出さない。
    [Theory]
    [MemberData(nameof(Entrances))]
    public async Task A_grant_is_not_logged_as_a_warning(string entrance)
    {
        var (client, log) = Answering(granted: true);

        (await ResolveGrantsAsync(client, entrance)).Should().BeTrue();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 固定の応答か、固定の失敗を返す最小の呼び出し器。
    private sealed class FixedInvoker(Pb.ResolveScopeResponse? response = null, StatusCode? failWith = null) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var task = failWith is { } status
                ? Task.FromException<TResponse>(new RpcException(new Status(status, "fake")))
                : Task.FromResult((TResponse)(object)response!);
            return new AsyncUnaryCall<TResponse>(task, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
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
}
