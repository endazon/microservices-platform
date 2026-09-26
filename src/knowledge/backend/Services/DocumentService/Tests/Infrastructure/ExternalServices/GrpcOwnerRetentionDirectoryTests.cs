using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-19, UC-11, SC-19, NFR-09, ADR-0057 決定 2, ADR-0096 決定 1, [[IADR-0428]] 決定 3,
// [[IADR-0431]], [[IADR-0474]] 決定 6 (#1532, #1583):
// **退職者の個人資料の完全削除を決める gRPC 写像**を固定する。
//
// 🔴 本クラスが無かった。IADR-0431 の口は #1532 で document-service へ配線されるまで配備で動いておらず、
// 写像は 1 本も試験されていなかった —— `_ => NotEvaluable` を `_ => Elapsed` に変える変異（M6）が
// 既存の 596 件をすべて通り抜けた（監査で実測）。その 1 行が、本項目を知らない古い認可サービス
// （found=true・enabled=false・eligibility=Unspecified がすべて既定値で返る）による**一斉削除**を止める唯一の門である。
// 誤削除は取り返せない（ADR-0057 決定 2）ので、「消さない」側の各枝を 1 本ずつ別の試験に置く。
//
// 🔴 陽性対照（名簿が明示的に Elapsed と言えば削除対象）を必ず対で置く —— 常に null を返す実装でも陰性は緑になる。
[Trait("TestKind", "Unit")]
public class GrpcOwnerRetentionDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GrpcOwnerRetentionDirectory Directory(FakeUserDirectoryClient fake, TimeSpan? timeout = null)
    {
        var client = new UserDirectoryGrpcClient(fake, NullLogger<UserDirectoryGrpcClient>.Instance);
        return timeout is { } t ? new GrpcOwnerRetentionDirectory(client, t) : new GrpcOwnerRetentionDirectory(client);
    }

    private static Pb.GetUserAttributesResponse Departed(Pb.RetentionEligibility eligibility) => new()
    {
        Found = true,
        Username = "carol",
        Enabled = false,
        RetentionEligibility = eligibility,
    };

    // T-RD-01 陽性対照: 名簿に居て・無効化済みで・名簿が**明示的に** Elapsed と言ったときだけ削除対象。
    [Fact]
    public async Task 名簿が明示的に経過と答えた無効化済みの所有者だけが削除対象になる()
    {
        var fake = FakeUserDirectoryClient.Answering(Departed(Pb.RetentionEligibility.Elapsed));

        var status = await Directory(fake).GetAsync("carol", Ct);

        status.Should().Be(new OwnerRetentionStatus(true, false, OwnerRetentionEligibility.Elapsed));
        status!.IsPurgeable.Should().BeTrue();
        fake.LastUsername.Should().Be("carol");
    }

    // T-RD-02 🔴 M6 を殺す試験: `Unspecified`（proto3 の既定 0）は NotEvaluable であり、削除しない。
    [Fact]
    public async Task 窓の判定が未指定なら数えておらず削除しない()
    {
        var fake = FakeUserDirectoryClient.Answering(Departed(Pb.RetentionEligibility.Unspecified));

        var status = await Directory(fake).GetAsync("carol", Ct);

        status!.Eligibility.Should().Be(OwnerRetentionEligibility.NotEvaluable);
        status.IsPurgeable.Should().BeFalse();
    }

    // T-RD-03 🔴 古い認可サービスの応答（found 以外すべて既定値）を丸ごと再現する。
    // enabled=false（既定）・eligibility=Unspecified（既定）—— 「無効化済みで窓が閉じた」に見えてはならない。
    [Fact]
    public async Task 窓の項目を知らない古い認可サービスの応答では削除しない()
    {
        var fake = FakeUserDirectoryClient.Answering(new Pb.GetUserAttributesResponse { Found = true, Username = "carol" });

        var status = await Directory(fake).GetAsync("carol", Ct);

        status!.IsPurgeable.Should().BeFalse("既定値だけの応答で一斉削除が起きてはならない");
    }

    // T-RD-04: 将来増えた未知の値も NotEvaluable（新しい値が削除側へ倒れない）。
    [Fact]
    public async Task 未知の窓の判定は数えておらず削除しない()
    {
        var fake = FakeUserDirectoryClient.Answering(Departed((Pb.RetentionEligibility)99));

        var status = await Directory(fake).GetAsync("carol", Ct);

        status!.Eligibility.Should().Be(OwnerRetentionEligibility.NotEvaluable);
        status.IsPurgeable.Should().BeFalse();
    }

    // T-RD-05: まだ窓の中なら削除しない。
    [Fact]
    public async Task 窓の中なら削除しない()
    {
        var fake = FakeUserDirectoryClient.Answering(Departed(Pb.RetentionEligibility.WithinWindow));

        var status = await Directory(fake).GetAsync("carol", Ct);

        status!.Eligibility.Should().Be(OwnerRetentionEligibility.WithinWindow);
        status.IsPurgeable.Should().BeFalse();
    }

    // T-RD-06: 名簿が Elapsed と言っても、在籍中（enabled=true）なら削除しない。
    [Fact]
    public async Task 在籍中の所有者は窓の判定が経過でも削除しない()
    {
        var answer = Departed(Pb.RetentionEligibility.Elapsed);
        answer.Enabled = true;
        var fake = FakeUserDirectoryClient.Answering(answer);

        (await Directory(fake).GetAsync("carol", Ct))!.IsPurgeable.Should().BeFalse();
    }

    // T-RD-07: 名簿に居ない（found=false）なら削除しない —— 同期漏れを「退職した」と読まない。
    [Fact]
    public async Task 名簿に居ない所有者は削除しない()
    {
        var fake = FakeUserDirectoryClient.Answering(new Pb.GetUserAttributesResponse
        {
            Found = false,
            RetentionEligibility = Pb.RetentionEligibility.Elapsed,
        });

        var status = await Directory(fake).GetAsync("ghost", Ct);

        status!.Found.Should().BeFalse();
        status.IsPurgeable.Should().BeFalse();
    }

    // T-RD-08: 名簿を引けない（輸送の失敗・全 status）→ null（「引けなかった」＝削除しない）。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 名簿を引けなければ判定なしを返し削除しない(StatusCode status)
    {
        var fake = FakeUserDirectoryClient.Failing(status);

        (await Directory(fake).GetAsync("carol", Ct)).Should().BeNull();
    }

    // T-RD-09: 名簿が応答しない → 上限（試験では 50 ms）で打ち切って null。日次の定期処理を止めない。
    // 🔴 試験自身にも期限を置く（上限を外す変異では偽の名簿が永久に応答せず、赤ではなく止まるため）。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 名簿が応答しなければ上限で打ち切って判定なしを返す(bool asRpcException)
    {
        var fake = FakeUserDirectoryClient.Hanging(asRpcException);

        (await Directory(fake, TimeSpan.FromMilliseconds(50)).GetAsync("carol", Ct)).Should().BeNull();
    }

    // T-RD-13 🔴 M8 を殺す試験（#1583）: 定期処理そのもの（呼び出し元）が取り消されたら、
    // 「引けなかった」（null）に畳まず取り消しを伝える（`GrpcOwnerRetentionDirectory` の `null` の枝の
    // `ct.ThrowIfCancellationRequested()`）。
    // 🔴 本番のチャネルは取り消しを `RpcException(Cancelled)` で投げ、共有クライアントがそれを null に畳む。
    // **その形（true）でしか M8 は見えない** —— `OperationCanceledException` の形（false）は catch の
    // `when (!ct.IsCancellationRequested)` を素通りして、変異の有無に関わらず伝わる。両方を置いて向きを固定する。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 定期処理そのものが取り消されたら取り消しをそのまま伝える(bool asRpcException)
    {
        var fake = FakeUserDirectoryClient.Hanging(asRpcException);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => Directory(fake, TimeSpan.FromMinutes(1)).GetAsync("carol", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-RD-14（#1583）: 退職の窓の実装は**退職の窓の読み口**（`GetRetentionStatusAsync`）を使う。
    // 2 つの読み口は応答の写し方が同じなので、失敗時のログの文言でしか区別できない。
    [Fact]
    public async Task 退職の窓の読み口を使い失敗を削除しない旨で記録する()
    {
        var logger = new RecordingLogger<UserDirectoryGrpcClient>();
        var client = new UserDirectoryGrpcClient(FakeUserDirectoryClient.Failing(StatusCode.Unavailable), logger);

        (await new GrpcOwnerRetentionDirectory(client).GetAsync("carol", Ct)).Should().BeNull();

        var warn = logger.OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Message.Should().Contain("退職の窓").And.Contain("削除しません");
        warn.Message.Should().NotContain("同期");
    }

    // T-RD-10: 本番の上限は 5 秒（同期の口と同じ）。
    [Fact]
    public void 本番の上限は5秒である()
        => GrpcOwnerRetentionDirectory.LookupTimeout.Should().Be(TimeSpan.FromSeconds(5));

    // T-RD-11: 口が未構成の配備の縮退は常に null（1 件も削除しない）。
    [Fact]
    public async Task 未構成の縮退は常に判定なしを返す()
        => (await new UnavailableOwnerRetentionDirectory().GetAsync("carol", Ct)).Should().BeNull();

    // T-RD-12 🔴 列挙の既定値（0）は NotEvaluable —— 初期化漏れ・写し損ねが「窓が閉じた」＝削除にならない。
    [Fact]
    public void 窓の判定の既定値は削除対象にならない()
    {
        default(OwnerRetentionEligibility).Should().Be(OwnerRetentionEligibility.NotEvaluable);
        new OwnerRetentionStatus(Found: true, Enabled: false, default).IsPurgeable.Should().BeFalse();
    }
}
