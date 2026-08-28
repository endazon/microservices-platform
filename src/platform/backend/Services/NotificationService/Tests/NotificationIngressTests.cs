using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features.Notifications;
using NotificationService.Domain;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Tests;

// FR-22, IADR-0215 決定 5, IADR-0270 決定 6: 通知の受け口（POST /internal/notifications）。
//
// 発火の**検知**は DocumentService（データの在る側）が行い、本サービスは受けて永続化する。
// ★ **否定形と境界を主役に置く。** 「正しいペイロードが 201 になる」だけのテストは、
// **何でも受理して何でも作るコードでも緑になる**。
//
// **器はテストメソッドごとに作り直す**（他のテストの seed が件数の表明を揺らさないため）。
public class NotificationIngressTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly DateTimeOffset Occurred = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    // 🔴 送信側 DocumentService.Infrastructure.ExternalServices.HttpPrivateNoteNotifier.IngressPath の値。
    // platform → knowledge の参照は禁止のため定数を共有できない。**リテラルで書き、一致を固定する。**
    private const string SenderIngressPath = "/internal/notifications";

    // 送信側が送るのと同じ形（匿名オブジェクト・camelCase）。**自由文の項目は 1 つも無い。**
    private static object Payload(
        string subject = "alice",
        string kind = NotificationKinds.PrivateNotePurgeWeekly,
        DateTimeOffset? occurredAt = null,
        int? count = 3,
        int? thresholdPercent = null,
        DateTimeOffset? deadline = null)
        => new
        {
            subject,
            kind,
            occurredAt = occurredAt ?? Occurred,
            count,
            thresholdPercent,
            deadline,
        };

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

    // FR-22: **送信側と同じパス・同じ形のペイロードを受理し、既存のドメインへ落とす。**
    [Fact]
    public async Task 送信側と同じ形のペイロードを受理して通知を永続化する()
    {
        var deadline = Occurred.AddDays(30);

        var response = await _factory.CreateClient().PostAsJsonAsync(
            SenderIngressPath, Payload(count: 4, deadline: deadline),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content
            .ReadFromJsonAsync<NotificationIngressResultDto>(TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
        result!.Id.Should().NotBe(Guid.Empty);
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
        await _factory.CreateClient().PostAsJsonAsync(
            SenderIngressPath, Payload(subject: "alice"), TestContext.Current.CancellationToken);

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

    // FR-22, IADR-0270 決定 6: **パスの複製が食い違っていないことを固定する。**
    // platform → knowledge の参照が張れないため、この一致を守る機械はこのテストだけである。
    [Fact]
    public async Task 受け口のパスは送信側の宣言と同じ値である()
    {
        NotificationIngressEndpoints.IngressPath.Should().Be(SenderIngressPath,
            "★ 送信側 HttpPrivateNoteNotifier.IngressPath と 1 バイトでも違えば通知は届かない");

        var response = await _factory.CreateClient().PostAsJsonAsync(
            SenderIngressPath, Payload(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "宣言したパスで到達できること");
    }

    // FR-22: **不正なペイロードは 400。** 欠落を既定値で埋めて受理しない。
    public static TheoryData<string, string> InvalidPayloads()
    {
        var longSubject = new string('a', NotificationIngress.SubjectMaxLength + 1);
        var longKind = new string('k', NotificationIngress.KindMaxLength + 1);
        const string When = "\"occurredAt\":\"2026-08-28T09:00:00+00:00\"";

        return new TheoryData<string, string>
        {
            { "subject 欠落", $"{{\"kind\":\"x\",{When}}}" },
            { "subject 空白", $"{{\"subject\":\"   \",\"kind\":\"x\",{When}}}" },
            { "subject 長すぎ", $"{{\"subject\":\"{longSubject}\",\"kind\":\"x\",{When}}}" },
            { "kind 欠落", $"{{\"subject\":\"alice\",{When}}}" },
            { "kind 空白", $"{{\"subject\":\"alice\",\"kind\":\"\",{When}}}" },
            { "kind 長すぎ", $"{{\"subject\":\"alice\",\"kind\":\"{longKind}\",{When}}}" },
            { "occurredAt 欠落", "{\"subject\":\"alice\",\"kind\":\"x\"}" },
            { "count が負", $"{{\"subject\":\"alice\",\"kind\":\"x\",{When},\"count\":-1}}" },
            { "thresholdPercent が範囲外", $"{{\"subject\":\"alice\",\"kind\":\"x\",{When},\"thresholdPercent\":101}}" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task 不正なペイロードは400を返す(string label, string json)
    {
        var response = await _factory.CreateClient().PostAsync(
            SenderIngressPath, new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"「{label}」は受理してはならない");
    }

    // FR-22: **400 のときは 1 件も永続化しない。** 状態コードだけを見るテストは、
    // 400 を返しつつ裏で壊れた通知を書くコードを見逃す。
    [Fact]
    public async Task 不正なペイロードは通知を1件も作らない()
    {
        var response = await _factory.CreateClient().PostAsync(
            SenderIngressPath,
            new StringContent("{\"kind\":\"x\",\"occurredAt\":\"2026-08-28T09:00:00+00:00\"}",
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await NotificationsAsync()).Should().BeEmpty("★ 検証は永続化より前に効くこと");
        (await OutboxAsync()).Should().BeEmpty();
    }

    // FR-22: **同一事象の再送は畳む**（送信側は失敗時に再送し得る）。
    [Fact]
    public async Task 同一ペイロードの再送は畳まれて通知は1件のままである()
    {
        var client = _factory.CreateClient();
        var payload = Payload(count: 2, deadline: Occurred.AddDays(30));

        var first = await client.PostAsJsonAsync(SenderIngressPath, payload, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(SenderIngressPath, payload, TestContext.Current.CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "再送は新規作成ではない");

        var firstResult = await first.Content
            .ReadFromJsonAsync<NotificationIngressResultDto>(TestContext.Current.CancellationToken);
        var secondResult = await second.Content
            .ReadFromJsonAsync<NotificationIngressResultDto>(TestContext.Current.CancellationToken);

        secondResult!.Duplicate.Should().BeTrue();
        secondResult.Id.Should().Be(firstResult!.Id, "畳んだ先の id を返す");

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
        var client = _factory.CreateClient();

        var warn80 = await client.PostAsJsonAsync(SenderIngressPath,
            Payload(kind: NotificationKinds.StorageQuotaWarning, count: null, thresholdPercent: 80),
            TestContext.Current.CancellationToken);
        var warn95 = await client.PostAsJsonAsync(SenderIngressPath,
            Payload(kind: NotificationKinds.StorageQuotaWarning, count: null, thresholdPercent: 95),
            TestContext.Current.CancellationToken);

        warn80.StatusCode.Should().Be(HttpStatusCode.Created);
        warn95.StatusCode.Should().Be(HttpStatusCode.Created, "★ 95% の警告が 80% の重複にされてはならない");

        var notifications = await NotificationsAsync();
        notifications.Should().HaveCount(2);
        notifications.Select(n => n.ThresholdPercent).Should().BeEquivalentTo([80, 95]);
    }

    // FR-22, IADR-0215 決定 2: **種別の値集合は開いている。** 閉じると「種別を増やしたら、
    // まだ更新されていない受け側が既存の値ごと拒否する」を受け口の側で再現してしまう。
    [Fact]
    public async Task 未知の種別も拒否せずに受理する()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            SenderIngressPath, Payload(kind: "some-future-kind"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await NotificationsAsync()).Should().ContainSingle(n => n.Kind == "some-future-kind");
    }

    // FR-22, IADR-0215 決定 4: **過去の期限も正当である**（期限切れの繰り越しは dispatcher が
    // dropped として記録する）。入口で弾くと、遅れて検知した削除通知が届かなくなる。
    [Fact]
    public async Task 過去の期限を持つ通知も受理する()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            SenderIngressPath, Payload(deadline: Occurred.AddDays(-1)),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // FR-22, IADR-0017（Superseded by IADR-0026）: **受け口は認証を課さない**（内部 API の慣行。
    // 呼び出し元はユーザー文脈を持たない定期処理であり JWT を持ち得ない）。第一防御はネットワーク
    // 境界側にある。**ただし読み出しは認証必須のままである** —— 無認証で作れることと、
    // 無認証で読めることは別である。
    [Fact]
    public async Task 受け口は無認証で到達できるが一覧は無認証では読めない()
    {
        var client = _factory.CreateClient();

        var ingress = await client.PostAsJsonAsync(
            SenderIngressPath, Payload(), TestContext.Current.CancellationToken);
        ingress.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetAsync("/notifications", TestContext.Current.CancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "★ 受け口を無認証にしたことが、読み出しの認証まで緩めていないこと");
    }
}
