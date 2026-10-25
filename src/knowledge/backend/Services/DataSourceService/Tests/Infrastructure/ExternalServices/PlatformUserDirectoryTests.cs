using AwesomeAssertions;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, SC-17, NFR-09, NFR-16, ADR-0029, ADR-0064 決定 4, ADR-0074 決定 4, ADR-0075,
// [[IADR-0329]], [[IADR-0379]] 決定 4, [[IADR-0401]] 決定 2・3 (#1255):
// **T-P2-04 / T-P2-05 / T-P2-06** —— 写像先の実在検証（gRPC 実装）を固定する。
// ［2026-10-10 / #1255］[[IADR-0533]]: REST 実装（`AuthorizationServiceUserDirectory`）を撤去したので、
// 「両実装で同じ答え」の表明は gRPC 実装の絶対値へ書き換え、REST にしか無い表明（非 2xx・利用者の
// `Authorization` の転送）は撤去した。
//
// 🔴 名簿を読む側が試験されないと、「引けなかった」を「実在しない」に化けさせる変異が緑のまま通る。
//
// 🔴 **陽性・陰性・縮退を必ず対にする。** 「保存されなかった」だけでは、実装が壊れているのか
// 検証が効いているのか区別できない。
[Trait("TestKind", "Unit")]
public class PlatformUserDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlySet<string> Ask(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static IPlatformUserDirectory Grpc(FakeUserDirectoryClient client) =>
        new GrpcPlatformUserDirectory(new UserDirectoryGrpcClient(
            client, NullLogger<UserDirectoryGrpcClient>.Instance));

    // 呼び出し先の照合規則（序数一致・無効化された利用者も実在）を偽物でも同じに保つ。
    private static IPlatformUserDirectory GrpcOk() =>
        Grpc(FakeUserDirectoryClient.Knowing(["alice", "bob", "carol"]));

    // ── T-P2-04: 陽性（実在）／陰性（不在） ───────────────────────────
    [Fact]
    public async Task Grpc_reports_the_existing_subset()
    {
        var grpc = await GrpcOk().LookupAsync(Ask("alice", "dave"), Ct);

        grpc.Available.Should().BeTrue();
        grpc.Usernames.Should().BeEquivalentTo(["alice"], "陽性は alice、陰性は dave");
    }

    // ── T-P2-04: 縮退（引けなかった） ───────────────────────────────
    // 🔴 **空集合と混ぜない。** `Available=false` は「利用者が 0 人」ではない ——
    // 呼び出し元はこれを 502 へ、実在しないを 400 へ写す。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Grpc_degrades_to_unavailable_on_rpc_failure(StatusCode status)
    {
        var snapshot = await Grpc(FakeUserDirectoryClient.Failing(status)).LookupAsync(Ask("alice"), Ct);

        snapshot.Available.Should().BeFalse();
        snapshot.Usernames.Should().BeEmpty();
    }

    // ── T-P2-05: 🔴 無効化された利用者も実在として数える ──────────────────────
    // ADR-0074 決定 4 が課すのは「実在すること」であって「有効であること」ではない
    // （退職者が所有者だった文書は所有者を失わない）。
    // 呼び出し先は無効化された利用者にも `exists=true` を返す（その規則は呼び出し先の試験が測る）。
    // 呼び出し側は `exists` だけを読み、有効かどうかで落とさないこと。
    [Fact]
    public async Task Disabled_user_still_counts_as_existing()
    {
        var grpc = await GrpcOk().LookupAsync(Ask("carol"), Ct);

        grpc.Usernames.Should().Contain("carol");
    }

    // ── T-P2-06: 🔴 照合は序数一致（大小文字違いは不在） ──────────────────────
    // 移行で照合規則を変えない（McpServer 側の大小文字無視との不一致はそのまま写す）。
    [Fact]
    public async Task Matching_is_ordinal()
    {
        (await GrpcOk().LookupAsync(Ask("ALICE"), Ct)).Usernames.Should().BeEmpty();

        // 陽性対照: 綴りが一致すれば実在する。
        (await GrpcOk().LookupAsync(Ask("alice"), Ct)).Usernames.Should().Contain("alice");
    }

    // 🔴 [[IADR-0401]] 決定 3: **口は「照会」である。** gRPC 実装は要求した名前だけを後段へ送る
    // （名簿の列挙を s2s の面へ出さないことの、呼び出し側から見た現れ）。
    // #1557 監査: 写像先の実在照会も締切つきで呼ぶ（後段が固まっても書き込みが 5 秒で 502 になる）。
    [Fact]
    public async Task Grpc_calls_with_a_short_deadline()
    {
        var fake = FakeUserDirectoryClient.Knowing(["alice"]);

        await Grpc(fake).LookupAsync(Ask("alice"), Ct);

        fake.LastDeadline.Should().NotBeNull();
        fake.LastDeadline!.Value.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Grpc_sends_only_the_requested_names()
    {
        var fake = FakeUserDirectoryClient.Knowing(["alice", "bob", "carol"]);

        await Grpc(fake).LookupAsync(Ask("alice", "dave"), Ct);

        fake.LastRequestedUsernames.Should().BeEquivalentTo(["alice", "dave"]);
        fake.CallCount.Should().Be(1);
    }

    // 🔴 空の照会では後段を呼ばない（無関係な操作を巻き込まない）。陽性対照は上の 1 本。
    [Fact]
    public async Task Grpc_does_not_call_the_backend_for_an_empty_query()
    {
        var fake = FakeUserDirectoryClient.Knowing(["alice"]);

        var snapshot = await Grpc(fake).LookupAsync(Ask(), Ct);

        snapshot.Available.Should().BeTrue();
        snapshot.Usernames.Should().BeEmpty();
        fake.CallCount.Should().Be(0);
    }

    // east-west gRPC の生成クライアントの偽物。**呼び出し先の照合規則（序数一致・無効も実在）を
    // 同じ形で写す** —— 偽物が甘いと、呼び出し元の試験が呼び出し先の規則を測らなくなる。
    internal sealed class FakeUserDirectoryClient : Pb.UserDirectory.UserDirectoryClient
    {
        private readonly IReadOnlySet<string>? _known;
        private readonly StatusCode? _failWith;

        private FakeUserDirectoryClient(IReadOnlySet<string>? known, StatusCode? failWith)
        {
            _known = known;
            _failWith = failWith;
        }

        public IReadOnlyList<string> LastRequestedUsernames { get; private set; } = [];

        public int CallCount { get; private set; }

        public DateTime? LastDeadline { get; private set; }

        public static FakeUserDirectoryClient Knowing(IEnumerable<string> usernames) =>
            new(new HashSet<string>(usernames, StringComparer.Ordinal), null);

        public static FakeUserDirectoryClient Failing(StatusCode status) => new(null, status);

        public override AsyncUnaryCall<Pb.CheckUsernamesResponse> CheckUsernamesAsync(
            Pb.CheckUsernamesRequest request, CallOptions options)
        {
            CallCount++;
            LastRequestedUsernames = [.. request.Usernames];
            LastDeadline = options.Deadline;

            if (_failWith is { } status)
            {
                var ex = new RpcException(new Status(status, "fake"));
                return new AsyncUnaryCall<Pb.CheckUsernamesResponse>(
                    Task.FromException<Pb.CheckUsernamesResponse>(ex), Task.FromResult(new Metadata()),
                    () => new Status(status, "fake"), () => [], () => { });
            }

            var resp = new Pb.CheckUsernamesResponse();
            foreach (var username in request.Usernames)
                resp.Results.Add(new Pb.UsernameExistence
                {
                    Username = username,
                    Exists = _known!.Contains(username),
                });

            return new AsyncUnaryCall<Pb.CheckUsernamesResponse>(
                Task.FromResult(resp), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
