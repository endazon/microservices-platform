using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features.Notifications;
using NotificationService.Domain;
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Tests.Features.Notifications;

// FR-22: 受け入れ基準「通知が所有者本人にのみ届く。他の利用者・管理者へは届かない」（AC-3）。
//
// ★ **否定形を主役に置く。** 「本人には届く」だけを確かめるテストは、**全員に配るコードでも緑になる**。
// 本クラスの中心は「alice の通知が bob には 1 件も現れない」「bob が alice の通知を既読化できない」である。
//
// **器はテストメソッドごとに作り直す**（IClassFixture で共有しない）。xUnit はテストごとに
// クラスの新しいインスタンスを作るので、ここで作れば InMemory の DB もテストごとに分かれる。
// 共有すると各テストの seed が積み上がり、**件数の表明が他のテストの影響で揺れる**（実測）。
[Trait("TestKind", "Integration")]
public class NotificationOwnerScopingTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, subject);
        return client;
    }

    private async Task<(Guid AliceId, Guid BobId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        var alice = Notification.Create("alice", NotificationKinds.PrivateNotePurgeWeekly,
            DateTimeOffset.UtcNow.AddMinutes(-10), count: 3, deadline: DateTimeOffset.UtcNow.AddDays(30));
        var bob = Notification.Create("bob", NotificationKinds.StorageQuotaWarning,
            DateTimeOffset.UtcNow.AddMinutes(-5), thresholdPercent: 80);

        db.Notifications.AddRange(alice, bob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (alice.Id, bob.Id);
    }

    // FR-22: **他人の通知は一覧に 1 件も現れない**（AC-3 の否定形）。
    [Fact]
    public async Task 一覧は他の利用者の通知を返さない()
    {
        var (aliceId, bobId) = await SeedAsync();

        var response = await ClientAs("bob").GetAsync("/notifications", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<NotificationListDto>(TestContext.Current.CancellationToken);
        list.Should().NotBeNull();

        list!.Items.Should().ContainSingle(i => i.Id == bobId, "本人の通知は届く");
        list.Items.Should().NotContain(i => i.Id == aliceId, "★ 他の利用者の通知は 1 件も届いてはならない");
    }

    // FR-22: **未読件数にも他人の分が混ざらない**（AC-3）。
    // 一覧の項目だけを絞って件数を全体から数えると、**バッジの数字だけが他人の分を漏らす**。
    [Fact]
    public async Task 未読件数は本人の分だけを数える()
    {
        await SeedAsync();

        var list = await ClientAs("bob")
            .GetFromJsonAsync<NotificationListDto>("/notifications", TestContext.Current.CancellationToken);

        list.Should().NotBeNull();
        list!.UnreadCount.Should().Be(1, "★ bob の未読は 1 件だけである（alice の分を数えない）");
    }

    // FR-22: **他人の通知 ID を指定した既読化は 404**（AC-3。存在秘匿のため 403 にしない）。
    [Fact]
    public async Task 他の利用者の通知は既読化できず404を返す()
    {
        var (aliceId, _) = await SeedAsync();

        var response = await ClientAs("bob")
            .PostAsync($"/notifications/{aliceId}/read", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "★ 権限が無いことを 403 で知らせると、他人の通知 ID の実在が漏れる");
    }

    // FR-22: **他人が既読化を試みても、所有者の通知は未読のまま残る**（AC-3）。
    // 状態コードだけを見るテストは、404 を返しつつ裏で書き換えるコードを見逃す。
    [Fact]
    public async Task 他の利用者の既読化の試みは通知の状態を変えない()
    {
        var (aliceId, _) = await SeedAsync();

        await ClientAs("bob").PostAsync($"/notifications/{aliceId}/read", null, TestContext.Current.CancellationToken);

        var aliceList = await ClientAs("alice")
            .GetFromJsonAsync<NotificationListDto>("/notifications", TestContext.Current.CancellationToken);

        aliceList!.Items.Single(i => i.Id == aliceId).Read.Should().BeFalse("★ 他人の操作で既読になってはならない");
        aliceList.UnreadCount.Should().Be(1);
    }

    // FR-22: **管理者ロールを持っていても他人の通知は読めない**（AC-3）。
    // 通知は「役割ではなく主体」で絞る（契約の x-roles: []）。ロールで開く抜け道が無いことを固定する。
    [Fact]
    public async Task 管理者ロールでも他の利用者の通知は読めない()
    {
        var (aliceId, _) = await SeedAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "bob");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin,platform-operator");

        var list = await client.GetFromJsonAsync<NotificationListDto>("/notifications", TestContext.Current.CancellationToken);

        list!.Items.Should().NotContain(i => i.Id == aliceId, "★ 管理者は通知の宛先を広げない");
    }

    // FR-22: 本人の既読化は成功し**冪等**である（AC-3 の肯定側 ＋ 通信仕様書の冪等性）。
    [Fact]
    public async Task 本人の既読化は成功し冪等である()
    {
        var (aliceId, _) = await SeedAsync();
        var client = ClientAs("alice");

        var first = await client.PostAsync($"/notifications/{aliceId}/read", null, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstResult = await first.Content.ReadFromJsonAsync<NotificationReadResultDto>(TestContext.Current.CancellationToken);
        firstResult!.UnreadCount.Should().Be(0);

        var second = await client.PostAsync($"/notifications/{aliceId}/read", null, TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "既読のものへもう一度呼んでも 200 である");
        var secondResult = await second.Content.ReadFromJsonAsync<NotificationReadResultDto>(TestContext.Current.CancellationToken);
        secondResult!.UnreadCount.Should().Be(0);
    }

    // FR-22: **未認証は 401**（主体が解決できない要求を「誰か」として扱わない）。
    [Fact]
    public async Task 未認証の一覧取得は401を返す()
    {
        var response = await _factory.CreateClient().GetAsync("/notifications", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
