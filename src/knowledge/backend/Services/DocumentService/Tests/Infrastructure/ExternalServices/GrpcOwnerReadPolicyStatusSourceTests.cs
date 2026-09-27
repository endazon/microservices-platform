using AwesomeAssertions;
using DocumentService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 2, [[IADR-0481]] (#1665): 門が問う口の縮退。
// 🔴 失敗・時間切れ・s2s トークンの失敗・未構成はすべて「数えられない（null）」＝門は開かない。
//    呼び出し元（常駐）の取り消しだけは取り消しとして伝える。
public class GrpcOwnerReadPolicyStatusSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeClient(Func<CancellationToken, Task<Pb.GetOwnerReadPolicyStatusResponse>> respond)
        : Pb.AuthzScope.AuthzScopeClient
    {
        public override AsyncUnaryCall<Pb.GetOwnerReadPolicyStatusResponse> GetOwnerReadPolicyStatusAsync(
            Pb.GetOwnerReadPolicyStatusRequest request, CallOptions options)
            => new(respond(options.CancellationToken), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }

    private static GrpcOwnerReadPolicyStatusSource Source(
        Func<CancellationToken, Task<Pb.GetOwnerReadPolicyStatusResponse>> respond,
        RecordingLogger<GrpcOwnerReadPolicyStatusSource>? log = null, TimeSpan? timeout = null)
        => new(new FakeClient(respond), log ?? new RecordingLogger<GrpcOwnerReadPolicyStatusSource>(),
            timeout ?? TimeSpan.FromMinutes(1));

    // T-52: 応答の件数をそのまま返す（0 は「無い」）。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 件数をそのまま返す(int count)
        => (await Source(_ => Task.FromResult(new Pb.GetOwnerReadPolicyStatusResponse { ActiveCount = count }))
                .GetActiveCountAsync(Ct))
            .Should().Be(count);

    // T-52: 失敗は「数えられない」。古い認可サービス（rpc が無い）の UNIMPLEMENTED も同じ（配備の順序を誤っても開かない）。
    [Theory]
    [InlineData(StatusCode.Unimplemented)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Internal)]
    public async Task RPCの失敗は数えられない(StatusCode status)
    {
        var log = new RecordingLogger<GrpcOwnerReadPolicyStatusSource>();
        (await Source(_ => throw new RpcException(new Status(status, "fake")), log).GetActiveCountAsync(Ct))
            .Should().BeNull();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task s2sトークンを取れなければ数えられない()
        => (await Source(_ => throw new InvalidOperationException("no token")).GetActiveCountAsync(Ct)).Should().BeNull();

    // T-52: 応答しなければ自分の上限で打ち切って「数えられない」（常駐の ct は立っていない）。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 応答しなければ上限で打ち切って数えられない(bool asRpcException)
        => (await Source(ct => Hang(ct, asRpcException), timeout: TimeSpan.FromMilliseconds(50)).GetActiveCountAsync(Ct))
            .Should().BeNull();

    // 対照: 呼び出し元（常駐の停止）の取り消しは取り消しとして伝える。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 呼び出し元の取り消しはそのまま伝える(bool asRpcException)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => Source(ct => Hang(ct, asRpcException)).GetActiveCountAsync(cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task 未構成の縮退は常に数えられない()
        => (await new UnavailableOwnerReadPolicyStatusSource().GetActiveCountAsync(Ct)).Should().BeNull();

    private static async Task<Pb.GetOwnerReadPolicyStatusResponse> Hang(CancellationToken ct, bool asRpcException)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (asRpcException)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "fake"));
        }
        throw new InvalidOperationException("unreachable");
    }
}
