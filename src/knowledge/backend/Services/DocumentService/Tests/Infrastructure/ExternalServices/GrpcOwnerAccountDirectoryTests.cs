using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-20, SC-17, NFR-09, NFR-14, ADR-0029, ADR-0075, 計画 ADR-0114 決定 1・2,
// [[IADR-0401]] 決定 2, [[IADR-0474]] (#1532):
// 同期トークンの所有者のアカウント状態の gRPC 実装が、名簿の応答を**通すのは Enabled だけ**へ写すことを固定する。
//
// 🔴 陽性（有効 → Enabled）を陰性と対で置く —— 常に Unknown を返す実装でも陰性だけは緑になる。
[Trait("TestKind", "Unit")]
public class GrpcOwnerAccountDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GrpcOwnerAccountDirectory Directory(FakeUserDirectoryClient fake, TimeSpan? timeout = null)
    {
        var client = new UserDirectoryGrpcClient(fake, NullLogger<UserDirectoryGrpcClient>.Instance);
        return timeout is { } t ? new GrpcOwnerAccountDirectory(client, t) : new GrpcOwnerAccountDirectory(client);
    }

    // T-AS-11 陽性対照: 名簿に居て有効 → Enabled。**問い合わせは所有者の ID で行う。**
    [Fact]
    public async Task 名簿に居て有効なら_Enabled_を返し所有者のIDで引く()
    {
        var fake = FakeUserDirectoryClient.Answering(new Pb.GetUserAttributesResponse
        {
            Found = true,
            Username = "alice",
            Enabled = true,
        });

        (await Directory(fake).GetStateAsync("alice", Ct)).Should().Be(OwnerAccountState.Enabled);
        fake.LastUsername.Should().Be("alice");
    }

    // T-AS-12: 名簿に居るが無効化されている → Disabled。
    [Fact]
    public async Task 無効化されていれば_Disabled_を返す()
    {
        var fake = FakeUserDirectoryClient.Answering(new Pb.GetUserAttributesResponse
        {
            Found = true,
            Username = "carol",
            Enabled = false,
        });

        (await Directory(fake).GetStateAsync("carol", Ct)).Should().Be(OwnerAccountState.Disabled);
    }

    // T-AS-13: 名簿に居ない → NotFound（「引けなかった」とは別）。
    // 🔴 `found=false` の応答は `enabled` も既定 false だが、Disabled へ畳まない（ログ・試験で区別する）。
    [Fact]
    public async Task 名簿に居なければ_NotFound_を返す()
    {
        var fake = FakeUserDirectoryClient.Answering(new Pb.GetUserAttributesResponse { Found = false });

        (await Directory(fake).GetStateAsync("ghost", Ct)).Should().Be(OwnerAccountState.NotFound);
    }

    // T-AS-14: 輸送の失敗（全 status）→ Unknown。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 名簿を引けなければ_Unknown_を返す(StatusCode status)
    {
        var fake = FakeUserDirectoryClient.Failing(status);

        (await Directory(fake).GetStateAsync("alice", Ct)).Should().Be(OwnerAccountState.Unknown);
    }

    // T-AS-15（ADR-0114 決定 2 の「時間切れ」）: 名簿が応答しない → 上限で打ち切って Unknown。
    // gRPC の取り消し（`RpcException(Cancelled)`）と、gRPC の外（s2s トークン取得の途中など）の
    // `OperationCanceledException` の両方を測る。
    // 🔴 試験自身にも期限を置く —— 上限が外れる変異では、偽の名簿が永久に応答しないため
    // 試験が赤ではなく**止まる**（実測。ホストごと打ち切られ、結果が 1 件も数えられなかった）。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 名簿が応答しなければ上限で打ち切って_Unknown_を返す(bool asRpcException)
    {
        var fake = FakeUserDirectoryClient.Hanging(asRpcException);

        var state = await Directory(fake, TimeSpan.FromMilliseconds(50)).GetStateAsync("alice", Ct);

        state.Should().Be(OwnerAccountState.Unknown);
    }

    // T-AS-16: 呼び出し元（要求）自身が取り消されたら、Unknown に畳まず取り消しを伝える
    // （時間切れと要求の中断を混ぜない。どちらでも応答は返らないが、ログの意味が変わる）。
    // 🔴 本番のチャネルは取り消しを `RpcException(Cancelled)` で投げ、共有クライアントがそれを null に畳む
    // （true の形）。その形でも取り消しとして伝わることを測る。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 要求そのものが取り消されたら取り消しをそのまま伝える(bool asRpcException)
    {
        var fake = FakeUserDirectoryClient.Hanging(asRpcException);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => Directory(fake, TimeSpan.FromMinutes(1)).GetStateAsync("alice", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-AS-17: 未構成の縮退は常に Unknown（＝同期は 401）。
    [Fact]
    public async Task 未構成の縮退は常に_Unknown_を返す()
        => (await new UnavailableOwnerAccountDirectory().GetStateAsync("alice", Ct))
            .Should().Be(OwnerAccountState.Unknown);

    // T-AS-18: 列挙の既定値（0）は Unknown —— 初期化漏れが「通す」へ倒れない。
    [Fact]
    public void 状態の既定値は_Unknown_である()
        => default(OwnerAccountState).Should().Be(OwnerAccountState.Unknown);
}
