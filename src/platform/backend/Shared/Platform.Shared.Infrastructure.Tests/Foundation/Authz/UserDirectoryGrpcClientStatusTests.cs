using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Tests.Testing;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-19, FR-20, SC-17, NFR-14, ADR-0096 決定 1, 計画 ADR-0114 決定 2, IADR-0431, IADR-0474 (#1532):
// 利用者名簿の 1 人分の照会（`GetUserAttributes` の found / enabled / 退職の窓）は 2 つの呼び出し元が読む ——
// 退職者の個人資料の完全削除（日次）と、同期トークンの所有者の照会（同期要求ごと）。
// **応答の写し方は同じで、失敗時のログの文言だけが違う**ことを固定する。
//
// 🔴 同期の経路の失敗を「退職の窓…削除しません」と書くと、同期要求のたびに運用者を誤った方向へ導く。
[Trait("TestKind", "Unit")]
public class UserDirectoryGrpcClientStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 陽性対照: 2 つの読み口は同じ応答を同じ形へ写す。
    [Fact]
    public async Task 退職の窓と同期の読み口は同じ応答を同じ形へ写す()
    {
        var logger = new RecordingLogger<UserDirectoryGrpcClient>();
        var client = new UserDirectoryGrpcClient(new FakeClient(null), logger);

        var retention = await client.GetRetentionStatusAsync("carol", Ct);
        var account = await client.GetAccountStatusAsync("carol", Ct);

        retention.Should().Be(new PlatformUserRetentionStatus(true, false, Pb.RetentionEligibility.Elapsed));
        account.Should().Be(retention);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task 同期の読み口の失敗は退職の窓の文言で記録しない()
    {
        var logger = new RecordingLogger<UserDirectoryGrpcClient>();
        var client = new UserDirectoryGrpcClient(new FakeClient(StatusCode.Unavailable), logger);

        (await client.GetAccountStatusAsync("alice", Ct)).Should().BeNull();

        var warn = logger.OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Message.Should().Contain("同期").And.Contain("拒否");
        warn.Message.Should().NotContain("退職").And.NotContain("削除");
    }

    [Fact]
    public async Task 退職の窓の読み口の失敗は削除しない旨を記録する()
    {
        var logger = new RecordingLogger<UserDirectoryGrpcClient>();
        var client = new UserDirectoryGrpcClient(new FakeClient(StatusCode.Unavailable), logger);

        (await client.GetRetentionStatusAsync("alice", Ct)).Should().BeNull();

        var warn = logger.OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Message.Should().Contain("退職の窓").And.Contain("削除しません");
    }

    private sealed class FakeClient(StatusCode? failWith) : Pb.UserDirectory.UserDirectoryClient
    {
        public override AsyncUnaryCall<Pb.GetUserAttributesResponse> GetUserAttributesAsync(
            Pb.GetUserAttributesRequest request, CallOptions options)
        {
            var response = failWith is { } status
                ? Task.FromException<Pb.GetUserAttributesResponse>(new RpcException(new Status(status, "fake")))
                : Task.FromResult(new Pb.GetUserAttributesResponse
                {
                    Found = true,
                    Username = request.Username,
                    Enabled = false,
                    RetentionEligibility = Pb.RetentionEligibility.Elapsed,
                });
            return new AsyncUnaryCall<Pb.GetUserAttributesResponse>(
                response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
