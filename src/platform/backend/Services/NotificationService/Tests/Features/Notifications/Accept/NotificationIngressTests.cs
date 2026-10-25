using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.Accept;
using NotificationService.Domain;
using NotificationService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace NotificationService.Tests.Features.Notifications.Accept;

// FR-22, IADR-0215 決定 5, IADR-0270 決定 6: 通知の受け口。
//
// ［2026-10-15 / #1902・[[IADR-0533]]］REST の受け口（`POST /internal/notifications`）は撤去した ——
// 送信側（DocumentService の N-1）は east-west gRPC（`NotificationIngress/Accept`）だけになっていた。
// 本クラスは同じ本体（`NotificationIngress.AcceptAsync`）を呼ぶ **gRPC 面**を TestServer 上で叩き、
// 受け口が守ってきた性質（永続化・本人限定・検証・重複の畳み・開いた種別・過去の期限）をそのまま固定する。
// 状態の写像は REST の 201 / 200 / 400 → OK（duplicate=false）/ OK（duplicate=true）/ INVALID_ARGUMENT。
//
// 発火の**検知**は DocumentService（データの在る側）が行い、本サービスは受けて永続化する。
// ★ **否定形と境界を主役に置く。** 「正しいペイロードが 201 になる」だけのテストは、
// **何でも受理して何でも作るコードでも緑になる**。
//
// **器はテストメソッドごとに作り直す**（他のテストの seed が件数の表明を揺らさないため）。
[Trait("TestKind", "Integration")]
public class NotificationIngressTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly DateTimeOffset Occurred = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    // 🔴 送信側（DocumentService）が名乗る s2s の主体。gRPC 面は `ServiceCaller`（`platform-service`）を要する。
    private Pb.NotificationIngress.NotificationIngressClient Ingress()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "service-account-document-service");
        http.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.ServiceRole);
        return new(GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions { HttpClient = http }));
    }

    // 送信側が送るのと同じ形。**自由文の項目は 1 つも無い。**
    private static Pb.AcceptRequest Payload(
        string subject = "alice",
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
            OccurredAt = Timestamp.FromDateTimeOffset(occurredAt ?? Occurred),
        };
        if (count is { } c) request.Count = c;
        if (thresholdPercent is { } p) request.ThresholdPercent = p;
        if (deadline is { } d) request.Deadline = Timestamp.FromDateTimeOffset(d);
        return request;
    }

    private Task<Pb.AcceptResponse> AcceptAsync(Pb.AcceptRequest request) =>
        Ingress().AcceptAsync(request, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    private async Task<List<Notification>> NotificationsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        return await db.Notifications.ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<EmailOutboxEntry>> OutboxAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        return await db.EmailOutbox.ToListAsync(TestContext.Current.CancellationToken);
    }

    // FR-22: **送信側と同じ形のペイロードを受理し、既存のドメインへ落とす。**
    [Fact]
    public async Task 送信側と同じ形のペイロードを受理して通知を永続化する()
    {
        var deadline = Occurred.AddDays(30);

        var result = await AcceptAsync(Payload(count: 4, deadline: deadline));

        result.Duplicate.Should().BeFalse();

        var notifications = await NotificationsAsync();
        notifications.Should().ContainSingle();
        notifications[0].Subject.Should().Be("alice");
        notifications[0].Kind.Should().Be(NotificationKinds.PrivateNotePurgeWeekly);
        notifications[0].Count.Should().Be(4);
        notifications[0].Deadline.Should().Be(deadline);
        notifications[0].OccurredAt.Should().Be(Occurred);
        notifications[0].Read.Should().BeFalse();

        // 配送は既存経路（NotificationPublisher）に乗る。**メール送出の実配線はしない**が、
        // outbox には積まれる（送出の結末は dispatcher が付ける。IADR-0215 決定 3）。
        var outbox = await OutboxAsync();
        outbox.Should().ContainSingle();
        outbox[0].Status.Should().Be(EmailOutboxStatus.Pending);
    }

    // FR-22: **受け口から入った通知も、読み出しは本人限定である**（AC-3 と繋がっていることの確認）。
    [Fact]
    public async Task 受け口から入った通知は本人の一覧にだけ現れる()
    {
        await AcceptAsync(Payload(subject: "alice"));

        var alice = _factory.CreateClient();
        alice.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "alice");
        var bob = _factory.CreateClient();
        bob.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "bob");

        var aliceList = await alice.GetFromJsonAsync<NotificationListDto>(
            "/notifications", TestContext.Current.CancellationToken);
        var bobList = await bob.GetFromJsonAsync<NotificationListDto>(
            "/notifications", TestContext.Current.CancellationToken);

        aliceList!.Items.Should().ContainSingle("宛先本人には届く");
        bobList!.Items.Should().BeEmpty("★ 受け口経由でも他人には 1 件も現れてはならない");
    }

    // FR-22: **不正なペイロードは INVALID_ARGUMENT**（REST の 400 に当たる）。欠落を既定値で埋めて受理しない。
    public static TheoryData<string, Pb.AcceptRequest> InvalidPayloads()
    {
        var longSubject = new string('a', NotificationIngress.SubjectMaxLength + 1);
        var longKind = new string('k', NotificationIngress.KindMaxLength + 1);
        var noOccurredAt = Payload(kind: "x");
        noOccurredAt.OccurredAt = null;

        return new TheoryData<string, Pb.AcceptRequest>
        {
            { "subject 欠落", Payload(subject: "", kind: "x") },
            { "subject 空白", Payload(subject: "   ", kind: "x") },
            { "subject 長すぎ", Payload(subject: longSubject, kind: "x") },
            { "kind 欠落", Payload(kind: "") },
            { "kind 長すぎ", Payload(kind: longKind) },
            { "occurredAt 欠落", noOccurredAt },
            { "count が負", Payload(kind: "x", count: -1) },
            { "thresholdPercent が範囲外", Payload(kind: "x", count: null, thresholdPercent: 101) },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task 不正なペイロードはINVALID_ARGUMENTを返す(string label, Pb.AcceptRequest request)
    {
        var act = () => AcceptAsync(request);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument, $"「{label}」は受理してはならない");
    }

    // FR-22: **不正なときは 1 件も永続化しない。** 状態だけを見るテストは、
    // 拒否しつつ裏で壊れた通知を書くコードを見逃す。
    [Fact]
    public async Task 不正なペイロードは通知を1件も作らない()
    {
        var act = () => AcceptAsync(Payload(subject: "", kind: "x"));

        await act.Should().ThrowAsync<RpcException>();
        (await NotificationsAsync()).Should().BeEmpty("★ 検証は永続化より前に効くこと");
        (await OutboxAsync()).Should().BeEmpty();
    }

    // FR-22: **同一事象の再送は畳む**（送信側は失敗時に再送し得る）。
    [Fact]
    public async Task 同一ペイロードの再送は畳まれて通知は1件のままである()
    {
        var payload = Payload(count: 2, deadline: Occurred.AddDays(30));

        var first = await AcceptAsync(payload);
        var second = await AcceptAsync(payload);

        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue("再送は新規作成ではない");

        (await NotificationsAsync()).Should().ContainSingle("★ 同じ通知が 2 行に増えてはならない");
        (await OutboxAsync()).Should().ContainSingle("メールも二重に積まない");
    }

    // FR-22, ADR-0037 決定 17: 🔴 **同一時刻・同一種別でも、閾値が違えば別の事象である。**
    // 容量警告は 80% と 95% を同一の検知時刻で同時に発火し得る（送信側は跨いだ閾値を順に送る）。
    // (subject, kind, occurredAt) の 3 項目で畳むと **95% の警告が 80% の重複として消える** ——
    // 「静かに落ちる」側の誤りであり、受け入れ基準が最も禁じている形である。
    [Fact]
    public async Task 同一時刻同一種別でも閾値が違えば別の通知として残る()
    {
        var warn80 = await AcceptAsync(
            Payload(kind: NotificationKinds.StorageQuotaWarning, count: null, thresholdPercent: 80));
        var warn95 = await AcceptAsync(
            Payload(kind: NotificationKinds.StorageQuotaWarning, count: null, thresholdPercent: 95));

        warn80.Duplicate.Should().BeFalse();
        warn95.Duplicate.Should().BeFalse("★ 95% の警告が 80% の重複にされてはならない");

        var notifications = await NotificationsAsync();
        notifications.Should().HaveCount(2);
        notifications.Select(n => n.ThresholdPercent).Should().BeEquivalentTo([80, 95]);
    }

    // FR-22, IADR-0215 決定 2: **種別の値集合は開いている。** 閉じると「種別を増やしたら、
    // まだ更新されていない受け側が既存の値ごと拒否する」を受け口の側で再現してしまう。
    [Fact]
    public async Task 未知の種別も拒否せずに受理する()
    {
        var response = await AcceptAsync(Payload(kind: "some-future-kind"));

        response.Duplicate.Should().BeFalse();
        (await NotificationsAsync()).Should().ContainSingle(n => n.Kind == "some-future-kind");
    }

    // FR-22, IADR-0215 決定 4: **過去の期限も正当である**（期限切れの繰り越しは dispatcher が
    // dropped として記録する）。入口で弾くと、遅れて検知した削除通知が届かなくなる。
    [Fact]
    public async Task 過去の期限を持つ通知も受理する()
    {
        var response = await AcceptAsync(Payload(deadline: Occurred.AddDays(-1)));

        response.Duplicate.Should().BeFalse();
        (await NotificationsAsync()).Should().ContainSingle();
    }

    // FR-22, NFR-09: 🔴 **受け口を通っても、読み出しは認証必須のままである**（作れることと読めることは別）。
    // ［2026-10-15 / #1902］撤去した REST の受け口は無認証だった。gRPC 面は `ServiceCaller` を要する
    // （`GrpcNotificationIngressTests` の T-04 / T-05）。
    [Fact]
    public async Task 受け口を通った通知も一覧は無認証では読めない()
    {
        await AcceptAsync(Payload());

        var list = await _factory.CreateClient().GetAsync("/notifications", TestContext.Current.CancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "★ 受け口が通ることが、読み出しの認証まで緩めていないこと");
    }

    // #1902: 撤去した REST の受け口はルート表に無い（戻ると、無認証で通知を作れる口が増える）。
    [Fact]
    public async Task 撤去したRESTの受け口はルート表に無い()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync(
            "/internal/notifications", new { subject = "alice", kind = "x", occurredAt = Occurred },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NotificationsAsync()).Should().BeEmpty();
    }
}
