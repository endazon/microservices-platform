using AwesomeAssertions;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, NFR-09, NFR-16, ADR-0029, ADR-0074 決定 4, 計画 ADR-0115 決定 5, [[IADR-0472]] (#1557):
// 部門コードの値域照会の **gRPC 実装**と**未宣言の縮退**を固定する（端点側は `DepartmentDomainEndpointTests`）。
//
// 🔴 **陽性・陰性・縮退を対にする**（`PlatformUserDirectoryTests` と同じ作法）。
// 「引けなかった」を「値域の外」に化けさせる変異は、端点の試験だけでは緑のまま通る（スタブが実装を代わりに務めるため）。
[Trait("TestKind", "Unit")]
public class DepartmentDomainDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlySet<string> Ask(params string[] codes) => new HashSet<string>(codes, StringComparer.Ordinal);

    private static IDepartmentDomainDirectory Grpc(FakeClient client) =>
        new GrpcDepartmentDomainDirectory(new UserDirectoryGrpcClient(client, NullLogger<UserDirectoryGrpcClient>.Instance));

    [Fact]
    public async Task Grpc_returns_only_the_codes_the_callee_reports_as_existing()
    {
        var snapshot = await Grpc(FakeClient.Knowing("sales", "hr")).LookupAsync(Ask("sales", "finance"), Ct);

        snapshot.Available.Should().BeTrue();
        snapshot.Codes.Should().BeEquivalentTo(["sales"]);
    }

    // 🔴 全 status を「引けなかった」へ倒す（UNAUTHENTICATED / PERMISSION_DENIED は配線の誤り、UNAVAILABLE は障害）。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.Internal)]
    public async Task Grpc_degrades_to_unavailable_on_rpc_failure(StatusCode status)
    {
        var snapshot = await Grpc(FakeClient.Failing(status)).LookupAsync(Ask("sales"), Ct);

        snapshot.Available.Should().BeFalse("引けなかったことを「値域の外」と報告しない");
        snapshot.Codes.Should().BeEmpty();
    }

    [Fact]
    public async Task Grpc_sends_only_the_requested_codes_and_skips_an_empty_query()
    {
        var fake = FakeClient.Knowing("sales");

        await Grpc(fake).LookupAsync(Ask("sales"), Ct);
        fake.LastRequested.Should().Equal("sales");

        var empty = await Grpc(fake).LookupAsync(Ask(), Ct);
        empty.Available.Should().BeTrue();
        fake.CallCount.Should().Be(1, "空の問いを後段へ投げない");
    }

    // #1557 監査: 🔴 **締切（deadline）を付けて呼ぶ。** 後段（Keycloak）が応答しないと管理者の書き込みが固まる。
    // 締切を過ぎれば DeadlineExceeded ＝ 上の「引けなかった」（502）へ倒れる。
    [Fact]
    public async Task Grpc_calls_with_a_short_deadline()
    {
        var fake = FakeClient.Knowing("sales");
        var before = DateTime.UtcNow;

        await Grpc(fake).LookupAsync(Ask("sales"), Ct);

        fake.LastDeadline.Should().NotBeNull();
        fake.LastDeadline!.Value.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(5)).And.BeAfter(before);
    }

    // 🔴 宛先が未宣言の配備は**常に「引けなかった」**である（値域の内へも外へも倒さない）。
    [Fact]
    public async Task Unconfigured_transport_always_reports_unavailable()
    {
        var snapshot = await new UnavailableDepartmentDomainDirectory().LookupAsync(Ask("sales"), Ct);

        snapshot.Available.Should().BeFalse();
    }

    // 生成クライアントの偽物。呼び出し先の照合規則（序数一致）を同じ形で写す。
    internal sealed class FakeClient : Pb.UserDirectory.UserDirectoryClient
    {
        private readonly IReadOnlySet<string>? _known;
        private readonly StatusCode? _failWith;

        private FakeClient(IReadOnlySet<string>? known, StatusCode? failWith)
        {
            _known = known;
            _failWith = failWith;
        }

        public IReadOnlyList<string> LastRequested { get; private set; } = [];

        public int CallCount { get; private set; }

        public DateTime? LastDeadline { get; private set; }

        public static FakeClient Knowing(params string[] codes) => new(new HashSet<string>(codes, StringComparer.Ordinal), null);

        public static FakeClient Failing(StatusCode status) => new(null, status);

        public override AsyncUnaryCall<Pb.CheckDepartmentCodesResponse> CheckDepartmentCodesAsync(
            Pb.CheckDepartmentCodesRequest request, CallOptions options)
        {
            CallCount++;
            LastRequested = [.. request.Codes];
            LastDeadline = options.Deadline;

            if (_failWith is { } status)
            {
                var ex = new RpcException(new Status(status, "fake"));
                return new AsyncUnaryCall<Pb.CheckDepartmentCodesResponse>(
                    Task.FromException<Pb.CheckDepartmentCodesResponse>(ex), Task.FromResult(new Metadata()),
                    () => new Status(status, "fake"), () => [], () => { });
            }

            var resp = new Pb.CheckDepartmentCodesResponse();
            foreach (var code in request.Codes)
                resp.Results.Add(new Pb.DepartmentCodeExistence { Code = code, Exists = _known!.Contains(code) });

            return new AsyncUnaryCall<Pb.CheckDepartmentCodesResponse>(
                Task.FromResult(resp), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
