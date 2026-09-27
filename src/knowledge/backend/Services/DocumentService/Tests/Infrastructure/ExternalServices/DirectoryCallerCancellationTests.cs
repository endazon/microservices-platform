using AwesomeAssertions;
using DocumentService.Infrastructure.ExternalServices;
using DocumentService.Tests.Grpc;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-05, FR-18, FR-19, FR-20, NFR-16, ADR-0029, [[IADR-0401]], [[IADR-0431]], [[IADR-0474]] (#1646):
// 認可サービスの共有クライアントを読む本サービスの 4 つの口が、**本物のチャネルで**呼び出し元の取り消しを
// 「要求そのものの token を持つ OCE」として伝えることを固定する。
//
// 🔴 共有クライアントは #1646 から呼び出し元の取り消しを `null` に畳まず、渡された token の OCE で外へ出す。
// 4 つの口はいずれも**上限つきの linked token**（`bounded.Token`）を渡すので、そのままでは外へ出る OCE の token が
// 要求の token と食い違う。各口は要求の token へ揃え直す。上限の時間切れ（要求は生きている）は従来どおり
// Unknown / `null` である（各口の既存の試験が固定する）。
[Trait("TestKind", "Unit")]
public class DirectoryCallerCancellationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Directories =>
    [
        nameof(GrpcOwnerAccountDirectory),
        nameof(GrpcOwnerRetentionDirectory),
        nameof(GrpcApproverRoleDirectory),
        nameof(GrpcDocumentReadScopeSource),
    ];

    [Theory]
    [MemberData(nameof(Directories))]
    public async Task 要求の取り消しは本物のチャネルでも要求のtokenを持つ取り消しとして伝わる(string directory)
    {
        var (users, scopes) = (new HangingUserDirectory(), new HangingAuthzScope());
        await using var server = directory == nameof(GrpcDocumentReadScopeSource)
            ? await LoopbackGrpcServer.StartAsync(scopes, Ct)
            : await LoopbackGrpcServer.StartAsync(users, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = Invoke(directory, server, cts.Token);
        await Task.WhenAny(users.Received.Task, scopes.Received.Task).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token, "上限つきの内側の token ではなく、要求そのものの取り消しとして伝える");
    }

    private static async Task Invoke(string directory, LoopbackGrpcServer server, CancellationToken ct)
    {
        var users = new UserDirectoryGrpcClient(
            new Pb.UserDirectory.UserDirectoryClient(server.Channel), NullLogger<UserDirectoryGrpcClient>.Instance);
        switch (directory)
        {
            case nameof(GrpcOwnerAccountDirectory):
                await new GrpcOwnerAccountDirectory(users).GetStateAsync("alice", ct);
                break;
            case nameof(GrpcOwnerRetentionDirectory):
                await new GrpcOwnerRetentionDirectory(users).GetAsync("alice", ct);
                break;
            case nameof(GrpcApproverRoleDirectory):
                await new GrpcApproverRoleDirectory(users).GetAdminStateAsync("alice", ct);
                break;
            case nameof(GrpcDocumentReadScopeSource):
                await new GrpcDocumentReadScopeSource(new AuthzScopeGrpcClient(
                        new Pb.AuthzScope.AuthzScopeClient(server.Channel), NullLogger<AuthzScopeGrpcClient>.Instance))
                    .ResolveReadBranchesAsync("alice", ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(directory), directory, "試験の器を更新すること");
        }
    }

    // 要求を受け取ったことを知らせてから、取り消されるまで応答しない受け口。
    private static async Task<T> Hang<T>(TaskCompletionSource received, ServerCallContext context)
    {
        received.TrySetResult();
        await Task.Delay(Timeout.Infinite, context.CancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private sealed class HangingUserDirectory : Pb.UserDirectory.UserDirectoryBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Pb.GetUserAttributesResponse> GetUserAttributes(
            Pb.GetUserAttributesRequest request, ServerCallContext context)
            => Hang<Pb.GetUserAttributesResponse>(Received, context);

        public override Task<Pb.CheckRealmRoleResponse> CheckRealmRole(Pb.CheckRealmRoleRequest request, ServerCallContext context)
            => Hang<Pb.CheckRealmRoleResponse>(Received, context);
    }

    private sealed class HangingAuthzScope : Pb.AuthzScope.AuthzScopeBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Pb.ResolveScopeResponse> Resolve(Pb.ResolveScopeRequest request, ServerCallContext context)
            => Hang<Pb.ResolveScopeResponse>(Received, context);
    }
}
