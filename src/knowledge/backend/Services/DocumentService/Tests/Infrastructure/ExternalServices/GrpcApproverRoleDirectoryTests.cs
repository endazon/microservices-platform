using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-18, NFR-09, SC-05, 計画 ADR-0088 決定 1, [[IADR-0401]] 追記, [[IADR-0410]] 追記 2 (#1636):
// 承認者が管理者かの gRPC 実装が、名簿の `CheckRealmRole` の応答を Admin / NotAdmin / Unknown へ写すことを固定する。
//
// 🔴 陽性（管理者 → Admin）を陰性と対で置く。🔴 引けなかったは Unknown（NotAdmin へ畳まない）。
[Trait("TestKind", "Unit")]
public class GrpcApproverRoleDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GrpcApproverRoleDirectory Directory(FakeRoleClient fake)
        => new(new UserDirectoryGrpcClient(fake, NullLogger<UserDirectoryGrpcClient>.Instance));

    [Fact]
    public async Task 名簿がロールを持つと答えればAdminで_問いは承認者の名前とplatform_adminで行う()
    {
        var fake = FakeRoleClient.Answering(found: true, hasRole: true);

        (await Directory(fake).GetAdminStateAsync("alice", Ct)).Should().Be(ApproverAdminState.Admin);
        fake.LastRequest!.Username.Should().Be("alice");
        fake.LastRequest.Role.Should().Be(PlatformAuthPolicies.AdminRole);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ロールを持たない_名簿に居ないならNotAdmin(bool found, bool hasRole)
    {
        (await Directory(FakeRoleClient.Answering(found, hasRole)).GetAdminStateAsync("bob", Ct))
            .Should().Be(ApproverAdminState.NotAdmin);
    }

    // 🔴 古い認可サービス（rpc を知らない ＝ UNIMPLEMENTED）・障害・門の拒否はすべて Unknown（管理者として通さない）。
    [Theory]
    [InlineData(StatusCode.Unimplemented)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 引けなければUnknown(StatusCode status)
    {
        (await Directory(FakeRoleClient.Failing(status)).GetAdminStateAsync("carol", Ct))
            .Should().Be(ApproverAdminState.Unknown);
    }

    // 要求そのものが取り消されたなら、障害と混ぜずに取り消しとして伝える。
    [Fact]
    public async Task 要求が取り消されたなら取り消しとして伝える()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Directory(FakeRoleClient.Failing(StatusCode.Cancelled)).GetAdminStateAsync("dave", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 未構成の縮退は常にUnknown()
        => (await new UnavailableApproverRoleDirectory().GetAdminStateAsync("alice", Ct))
            .Should().Be(ApproverAdminState.Unknown);

    [Fact]
    public void 状態の既定値はUnknownである()
        => default(ApproverAdminState).Should().Be(ApproverAdminState.Unknown);

    internal sealed class FakeRoleClient : Pb.UserDirectory.UserDirectoryClient
    {
        private readonly Pb.CheckRealmRoleResponse? _answer;
        private readonly StatusCode? _failWith;

        private FakeRoleClient(Pb.CheckRealmRoleResponse? answer, StatusCode? failWith)
        {
            _answer = answer;
            _failWith = failWith;
        }

        public Pb.CheckRealmRoleRequest? LastRequest { get; private set; }

        public static FakeRoleClient Answering(bool found, bool hasRole)
            => new(new Pb.CheckRealmRoleResponse { Found = found, HasRole = hasRole }, null);

        public static FakeRoleClient Failing(StatusCode status) => new(null, status);

        public override AsyncUnaryCall<Pb.CheckRealmRoleResponse> CheckRealmRoleAsync(
            Pb.CheckRealmRoleRequest request, CallOptions options)
        {
            LastRequest = request;
            var response = _failWith is { } status
                ? Task.FromException<Pb.CheckRealmRoleResponse>(new RpcException(new Status(status, "fake")))
                : Task.FromResult(_answer!);
            return new AsyncUnaryCall<Pb.CheckRealmRoleResponse>(
                response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
