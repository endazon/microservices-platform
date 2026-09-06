using System.Net;
using AwesomeAssertions;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DataSourceService.Tests.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, SC-17, NFR-09, NFR-16, ADR-0029, ADR-0064 決定 4, ADR-0074 決定 4, ADR-0075,
// [[IADR-0329]], [[IADR-0379]] 決定 4, [[IADR-0401]] 決定 2・3 (#1255):
// **T-P2-04 / T-P2-05 / T-P2-06** —— 写像先の実在検証の**2 実装**（REST と gRPC）を対で固定する。
//
// 🔴 **本クラスが無かった。** `AuthorizationServiceUserDirectory` は #1194 で入って以来
// 単体試験を 1 本も持たず、固定されていたのは `StubPlatformUserDirectory` 経由の端点側だけだった
// （陽性対照: 兄弟にあたる McpServer の `AuthorizationServiceRegistrarAttributes` は 19 件を持つ）。
// 名簿を読む側が試験されないと、「引けなかった」を「実在しない」に化けさせる変異が緑のまま通る。
//
// 🔴 **陽性・陰性・縮退を必ず対にする。** 「保存されなかった」だけでは、実装が壊れているのか
// 検証が効いているのか区別できない。
[Trait("TestKind", "Unit")]
public class PlatformUserDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 名簿の応答（`GET /authz/users`）。**`carol` は enabled=false**（退職者）である。
    private const string DirectoryJson = """
        [{"id":"u1","username":"alice","displayName":"Alice","enabled":true,"roles":[],"attributes":{}},
         {"id":"u2","username":"bob","displayName":"Bob","enabled":true,"roles":[],"attributes":{}},
         {"id":"u3","username":"carol","displayName":"Carol","enabled":false,"roles":[],"attributes":{}}]
        """;

    private static IReadOnlySet<string> Ask(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static IPlatformUserDirectory Rest(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new AuthorizationServiceUserDirectory(
            new StubHttpClientFactory(respond), new HttpContextAccessor(),
            NullLogger<AuthorizationServiceUserDirectory>.Instance);

    private static IPlatformUserDirectory RestOk() => Rest(_ => Json(DirectoryJson));

    private static IPlatformUserDirectory Grpc(FakeUserDirectoryClient client) =>
        new GrpcPlatformUserDirectory(new UserDirectoryGrpcClient(
            client, NullLogger<UserDirectoryGrpcClient>.Instance));

    // 呼び出し先の照合規則（序数一致・無効化された利用者も実在）を偽物でも同じに保つ。
    private static IPlatformUserDirectory GrpcOk() =>
        Grpc(FakeUserDirectoryClient.Knowing(["alice", "bob", "carol"]));

    // ── T-P2-04: 陽性（実在）／陰性（不在）を両実装で ───────────────────────────
    [Fact]
    public async Task Rest_and_grpc_report_the_same_existing_subset()
    {
        var asked = Ask("alice", "dave");

        var rest = await RestOk().LookupAsync(asked, Ct);
        var grpc = await GrpcOk().LookupAsync(asked, Ct);

        rest.Available.Should().BeTrue();
        rest.Usernames.Should().BeEquivalentTo(["alice"], "陽性は alice、陰性は dave");
        grpc.Should().BeEquivalentTo(rest);
    }

    // ── T-P2-04: 縮退（引けなかった）を両実装で ───────────────────────────────
    // 🔴 **空集合と混ぜない。** `Available=false` は「利用者が 0 人」ではない ——
    // 呼び出し元はこれを 502 へ、実在しないを 400 へ写す。
    [Fact]
    public async Task Rest_degrades_to_unavailable_on_non_2xx_and_transport_failure()
    {
        var status = await Rest(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))
            .LookupAsync(Ask("alice"), Ct);
        status.Available.Should().BeFalse();

        var refused = await Rest(_ => throw new HttpRequestException("refused"))
            .LookupAsync(Ask("alice"), Ct);
        refused.Available.Should().BeFalse();

        var timeout = await Rest(_ => throw new TaskCanceledException("timeout"))
            .LookupAsync(Ask("alice"), Ct);
        timeout.Available.Should().BeFalse();
    }

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
    [Fact]
    public async Task Disabled_user_still_counts_as_existing_in_both_implementations()
    {
        var asked = Ask("carol");

        var rest = await RestOk().LookupAsync(asked, Ct);
        var grpc = await GrpcOk().LookupAsync(asked, Ct);

        rest.Usernames.Should().Contain("carol");
        grpc.Usernames.Should().Contain("carol");
        DirectoryJson.Should().Contain("\"username\":\"carol\",\"displayName\":\"Carol\",\"enabled\":false",
            "陽性対照: 名簿側で実際に無効化されている");
    }

    // ── T-P2-06: 🔴 照合は序数一致（大小文字違いは不在） ──────────────────────
    // 移行で照合規則を変えない（McpServer 側の大小文字無視との不一致はそのまま写す）。
    [Fact]
    public async Task Matching_is_ordinal_in_both_implementations()
    {
        var asked = Ask("ALICE");

        (await RestOk().LookupAsync(asked, Ct)).Usernames.Should().BeEmpty();
        (await GrpcOk().LookupAsync(asked, Ct)).Usernames.Should().BeEmpty();

        // 陽性対照: 綴りが一致すれば実在する。
        (await RestOk().LookupAsync(Ask("alice"), Ct)).Usernames.Should().Contain("alice");
        (await GrpcOk().LookupAsync(Ask("alice"), Ct)).Usernames.Should().Contain("alice");
    }

    // 🔴 [[IADR-0401]] 決定 3: **口は「照会」である。** gRPC 実装は要求した名前だけを後段へ送る
    // （名簿の列挙を s2s の面へ出さないことの、呼び出し側から見た現れ）。
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

    // 🔴 REST 実装は利用者の `Authorization` を転送し、gRPC 実装は転送しない
    // （[[IADR-0401]] 決定 2。gRPC 側は s2s トークンだけで、面が狭められている）。
    [Fact]
    public async Task Rest_forwards_the_caller_authorization_header()
    {
        HttpRequestMessage? captured = null;
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.Request.Headers.Authorization = "Bearer user-token";

        var directory = new AuthorizationServiceUserDirectory(
            new StubHttpClientFactory(req => { captured = req; return Json(DirectoryJson); }),
            accessor, NullLogger<AuthorizationServiceUserDirectory>.Instance);

        await directory.LookupAsync(Ask("alice"), Ct);

        captured!.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.ToString().Should().Be("Bearer user-token");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
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

        public static FakeUserDirectoryClient Knowing(IEnumerable<string> usernames) =>
            new(new HashSet<string>(usernames, StringComparer.Ordinal), null);

        public static FakeUserDirectoryClient Failing(StatusCode status) => new(null, status);

        public override AsyncUnaryCall<Pb.CheckUsernamesResponse> CheckUsernamesAsync(
            Pb.CheckUsernamesRequest request, CallOptions options)
        {
            CallCount++;
            LastRequestedUsernames = [.. request.Usernames];

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
