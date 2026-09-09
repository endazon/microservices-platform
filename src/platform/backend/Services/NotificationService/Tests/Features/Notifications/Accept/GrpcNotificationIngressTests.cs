using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;
using NotificationService.Features.Notifications.Accept;
using NotificationService.Tests.Grpc;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace NotificationService.Tests.Features.Notifications.Accept;

// FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, UC-11, ADR-0004, ADR-0029, ADR-0037 決定 6・17・18,
// ADR-0045 決定 8, ADR-0075, [[IADR-0215]], [[IADR-0270]] 決定 6, [[IADR-0379]], [[IADR-0398]],
// [[IADR-0417]] 決定 8, [[IADR-0419]] (#1255):
// 通知の受け口の east-west gRPC 面（`platform.notification.v1.NotificationIngress`）を
// **実 Kestrel の h2c ポート**で往復し、s2s の検証と受理の判断が gRPC 経路でも保たれることを固定する。
//
// 陽性対照（T-01）と陰性対照（T-04 / T-05）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcNotificationIngressTests
{
    private const string ServiceSubject = "service-account-document-service";

    private readonly GrpcKestrelFactory _factory;

    public GrpcNotificationIngressTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Pb.NotificationIngress.NotificationIngressClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    // 主体はテストごとに一意にする（器はコレクションで共有され、台帳は積み上がる）。
    private static string NewSubject() => $"owner-{Guid.NewGuid():N}"[..24];

    private static Pb.AcceptRequest Request(
        string subject,
        string kind = NotificationKinds.PrivateNotePurgeWeekly,
        DateTimeOffset? occurredAt = null,
        int? count = 3,
        int? thresholdPercent = null,
        DateTimeOffset? deadline = null)
    {
        var request = new Pb.AcceptRequest
        {
            Subject = subject,
            Kind = kind,
            OccurredAt = Timestamp.FromDateTimeOffset(
                occurredAt ?? new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero)),
        };
        if (count is { } c) request.Count = c;
        if (thresholdPercent is { } p) request.ThresholdPercent = p;
        if (deadline is { } d) request.Deadline = Timestamp.FromDateTimeOffset(d);
        return request;
    }

    private async Task<List<Notification>> NotificationsAsync(string subject)
    {
        using var db = _factory.NewDbContext();
        return await db.Notifications.Where(n => n.Subject == subject)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    // 呼び出し側の共通部品（CreatePlatformChannel = 平文 h2c ＋ s2s CallCredentials）を実際に通す。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }

    // ── FR-22, NFR-16, [[IADR-0379]] 決定 3・4 ──

    // T-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで往復し、
    // **台帳に通知が 1 件積まれる**（＝面が REST と同じ本体を通っている）。
    [Fact]
    public async Task Accept_over_h2c_with_service_token_persists_the_notification()
    {
        var subject = NewSubject();
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new Pb.NotificationIngress.NotificationIngressClient(channel);

        var resp = await client.AcceptAsync(
            Request(subject), cancellationToken: TestContext.Current.CancellationToken);

        resp.Duplicate.Should().BeFalse("新規の受理である（REST の 201 と同値）");
        var stored = await NotificationsAsync(subject);
        stored.Should().ContainSingle()
            .Which.Kind.Should().Be(NotificationKinds.PrivateNotePurgeWeekly);
    }

    // T-02: gRPC 用ポートは HTTP/2 専用である（`Http1AndHttp2` ではない）。
    // 陽性対照: 同じ要求が HTTP/1.1 のポートでは 200 を返す（＝ gRPC を有効にしても 8080 相当は残る）。
    [Fact]
    public async Task Grpc_port_rejects_http11_while_http_port_still_serves()
    {
        using var http11 = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var onGrpcPort = await http11.GetAsync(
            $"{_factory.GrpcAddress}/health/live", TestContext.Current.CancellationToken);
        onGrpcPort.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "h2c 専用ポートは HTTP/1.1 の要求を処理しない");

        var onHttpPort = await http11.GetAsync(
            $"{_factory.HttpAddress}/health/live", TestContext.Current.CancellationToken);
        onHttpPort.StatusCode.Should().Be(HttpStatusCode.OK,
            "gRPC リスナを足しても HTTP/1.1 のポート（REST・受け口・/health/*）は消えない");
    }

    // T-03: 🔴 **REST の無認証の受け口は残っている**（[[IADR-0419]] 決定 4）。
    // gRPC 面へ `ServiceCaller` を掛けたことが、既存の呼び出し元を止めていないことの証明である。
    [Fact]
    public async Task The_rest_ingress_still_accepts_without_credentials()
    {
        var subject = NewSubject();
        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };

        var resp = await http.PostAsJsonAsync("/internal/notifications", new
        {
            subject,
            kind = NotificationKinds.PrivateNotePurgeWeekly,
            occurredAt = new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero),
            count = 3,
            thresholdPercent = (int?)null,
            deadline = (DateTimeOffset?)null,
        }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "★ 並走中の正は REST であり、無認証のまま残す");
        (await NotificationsAsync(subject)).Should().ContainSingle();
    }

    // T-04: 陰性対照。資格情報が無ければ `UNAUTHENTICATED`。
    [Fact]
    public async Task Accept_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().AcceptAsync(
            Request(NewSubject()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // T-05: 陰性対照。**利用者のトークン（管理者であっても）を転送しても通らない** ——
    // s2s の面は `platform-service` ロールだけを通す。これが緩むと confused deputy が成立する。
    [Fact]
    public async Task Accept_with_forwarded_admin_user_token_is_permission_denied()
    {
        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().AcceptAsync(
            Request(NewSubject()), headers: Bearer(userToken),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // ── FR-22, UC-11, [[IADR-0398]]: 受理の判断は REST と同じ関数を通る ──

    // T-06: 不正なペイロードは `INVALID_ARGUMENT`（REST の 400 と同値）で、**1 件も永続化されない**。
    // 🔴 「成功以外を成功に見せない」—— 呼び出し元は結末を計器へ載せており、ここで握ると沈黙する。
    [Fact]
    public async Task An_invalid_payload_is_invalid_argument_and_persists_nothing()
    {
        var subject = NewSubject();
        // subject が空白のみ ＝ 「subject は必須である。」
        var request = Request(subject);
        request.Subject = "   ";

        var act = async () => await PlainClient().AcceptAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain(NotificationIngressValidator.SubjectKey,
            "原因が追えるように鍵を status detail へ載せる");
        (await NotificationsAsync(subject)).Should().BeEmpty();
    }

    // T-07: 🔴 **同一ペイロードの再送は畳まれ、`duplicate=true` で返る**（REST の 200 と同値）。
    // 陽性対照が T-01（1 度目は false）である —— 片方だけでは「常に true」の実装が通る。
    [Fact]
    public async Task A_resend_of_the_same_payload_is_folded_and_reported_as_duplicate()
    {
        var subject = NewSubject();
        var request = Request(subject);

        var first = await PlainClient().AcceptAsync(
            request, headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var second = await PlainClient().AcceptAsync(
            request, headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue();
        (await NotificationsAsync(subject)).Should().ContainSingle("畳んだので台帳は 1 件のまま");
    }

    // T-08: 🔴 **`(subject, kind, occurredAt)` の 3 項目では畳まない。**
    // 容量警告は 80% と 95% を**同一の検知時刻で同時に発火し得る** ——
    // 3 項目で畳むと 95% の警告が 80% の重複として消える（FR-22 が最も禁じている「静かに落ちる」形）。
    [Fact]
    public async Task Two_quota_warnings_at_the_same_instant_are_both_accepted()
    {
        var subject = NewSubject();
        var at = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

        foreach (var percent in new[] { 80, 95 })
        {
            var resp = await PlainClient().AcceptAsync(
                Request(subject, NotificationKinds.StorageQuotaWarning, at,
                    count: null, thresholdPercent: percent),
                headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
            resp.Duplicate.Should().BeFalse($"{percent}% の警告は別の事象である");
        }

        (await NotificationsAsync(subject)).Should().HaveCount(2);
    }

    // T-09: 🔴 **presence を落とすと未設定が `0` に化ける。**
    // `count` を送らない要求は台帳の `Count` が `null` になり、`0` を送った要求とは**別の 1 件**になる。
    // これが崩れると（素の `int32` にする・`Has*` を読まない）、例外は 1 つも起きずに
    // **同じ事象が二重に積まれる**（重複判定だけが静かに割れる）。
    [Fact]
    public async Task An_absent_count_stays_absent_and_differs_from_zero()
    {
        var subject = NewSubject();
        var at = new DateTimeOffset(2026, 9, 9, 11, 0, 0, TimeSpan.Zero);

        var withoutCount = await PlainClient().AcceptAsync(
            Request(subject, NotificationKinds.PrivateNotePurgeDone, at, count: null),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        var withZero = await PlainClient().AcceptAsync(
            Request(subject, NotificationKinds.PrivateNotePurgeDone, at, count: 0),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        withoutCount.Duplicate.Should().BeFalse();
        withZero.Duplicate.Should().BeFalse("★ 0 は「未設定」ではない —— 畳まれてはならない");

        var stored = await NotificationsAsync(subject);
        stored.Should().HaveCount(2);
        stored.Select(n => n.Count).Should().BeEquivalentTo([null, (int?)0]);
    }

    // T-10: 🔴 **期限も presence で運ぶ。** 未設定は「期限なし」であって 0001-01-01 ではない。
    [Fact]
    public async Task An_absent_deadline_is_stored_as_null()
    {
        var subject = NewSubject();

        await PlainClient().AcceptAsync(
            Request(subject, NotificationKinds.PrivateNotePurgeWeekly, deadline: null),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await NotificationsAsync(subject)).Should().ContainSingle()
            .Which.Deadline.Should().BeNull();
    }

    // T-11: 🔴 **REST と gRPC は同じ事象を同じ 1 件に畳む**（＝同じ本体・同じ重複判定を通っている）。
    // 時刻は `Timestamp` が UTC へ正規化するが、`DateTimeOffset` の比較は瞬間で行われるので
    // オフセットの違いは畳みに影響しない。
    [Fact]
    public async Task Rest_and_grpc_fold_the_same_event_into_one()
    {
        var subject = NewSubject();
        var at = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var rest = await http.PostAsJsonAsync("/internal/notifications", new
        {
            subject,
            kind = NotificationKinds.SyncTokenExpiry,
            occurredAt = at,
            count = 2,
            thresholdPercent = (int?)null,
            deadline = (DateTimeOffset?)null,
        }, TestContext.Current.CancellationToken);
        rest.StatusCode.Should().Be(HttpStatusCode.Created, "★ 陽性対照 —— REST が先に 1 件作る");

        var grpc = await PlainClient().AcceptAsync(
            Request(subject, NotificationKinds.SyncTokenExpiry, at, count: 2),
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        grpc.Duplicate.Should().BeTrue("輸送を替えても同じ事象は同じ 1 件である");
        (await NotificationsAsync(subject)).Should().ContainSingle();
    }

    // T-12: 構造の門。gRPC サービス型が `ServiceCaller` ポリシーを宣言していること。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(NotificationIngressGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }
}
