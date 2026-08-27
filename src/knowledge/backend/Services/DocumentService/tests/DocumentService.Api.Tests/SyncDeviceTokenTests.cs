using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Endpoints;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using DocumentService.Api.Foundation.Services;
// #451-a: 個人資料・同期端末の応答 DTO は Knowledge.Contracts へ集約した（BFF と定義を 1 つにするため）。
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Api.Tests;

// FR-20, FR-22, ADR-0037 決定 10〜13・15・18, [[IADR-0270]] 決定 3:
// 同期トークンの発行・期限（30 日）・手動再発行・個別／一括失効・期限 7 日前通知の検知。
public class SyncDeviceTokenTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private HttpClient PluginWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // FR-20, ADR-0037 決定 11・12: 利用者が自ら発行でき、有効期限は 30 日である。
    // 平文トークンは発行応答にだけ現れ、一覧には現れない。
    [Fact]
    public async Task トークンは利用者が自ら発行でき期限は30日で一覧に平文は現れない()
    {
        var user = $"tok-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var before = DateTimeOffset.UtcNow;

        var resp = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "MacBook" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var issued = await resp.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();
        issued!.Token.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAt.Should().BeCloseTo(before.AddDays(SyncDevice.TokenLifetimeDays),
            TimeSpan.FromMinutes(5), "有効期限は 30 日");

        var listJson = await session.GetStringAsync("/private-notes/devices/");
        listJson.Should().NotContain(issued.Token, "平文トークンは発行応答で 1 回だけ返る");

        var devices = await session.GetFromJsonAsync<List<SyncDeviceDto>>(
            "/private-notes/devices/");
        devices.Should().ContainSingle(d => d.Id == issued.DeviceId && d.Active);
    }

    // ADR-0037 決定 12（否定形）＋陽性対照: 期限切れトークンは 401。期限内は通る。
    [Fact]
    public async Task 期限切れトークンは401になる()
    {
        var user = $"exp-{Guid.NewGuid():N}"[..20];
        var (expiredToken, expiredHash) = SyncTokens.Generate();
        var (validToken, validHash) = SyncTokens.Generate();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            // 31 日前に発行 → 期限切れ
            db.SyncDevices.Add(SyncDevice.Create(user, "old-pc", expiredHash,
                DateTimeOffset.UtcNow.AddDays(-31)));
            // 現在発行 → 有効（陽性対照）
            db.SyncDevices.Add(SyncDevice.Create(user, "new-pc", validHash,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        (await PluginWith(expiredToken).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "30 日の期限は失効操作を忘れた場合の最終的な歯止め");
        (await PluginWith(validToken).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // ADR-0037 決定 15: 手動再発行。旧トークンは即時無効になり、新トークンが 30 日で発行される。
    [Fact]
    public async Task 再発行すると旧トークンは即時無効になり新トークンが使える()
    {
        var user = $"rei-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var issued = await (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" })).Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();

        var reissued = await (await session.PostAsync(
            $"/private-notes/devices/{issued!.DeviceId}/reissue", null))
            .Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();
        reissued!.Token.Should().NotBe(issued.Token);

        (await PluginWith(issued.Token).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "旧トークンは再発行で無効になる");
        (await PluginWith(reissued.Token).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // ADR-0037 決定 13: 全端末の一括失効（端末紛失時、どの端末か特定できない場面の防御）。
    [Fact]
    public async Task 一括失効で全端末のトークンが同時に無効になる()
    {
        var user = $"all-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var t1 = await (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc-1" })).Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();
        var t2 = await (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc-2" })).Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();

        // 陽性対照: 失効前は両方使える
        (await PluginWith(t1!.Token).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var revoke = await session.PostAsync("/private-notes/devices/revoke-all", null);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        (await PluginWith(t1.Token).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await PluginWith(t2!.Token).GetAsync("/private-notes/sync/manifest")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // FR-20（否定形）: 他人の端末は一覧に現れず、失効・再発行もできない（存在秘匿の 404）。
    [Fact]
    public async Task 他人の端末は見えず失効も再発行もできない()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var mallory = SessionAs($"mallory-{suffix}");
        var victim = SessionAs($"victim-{suffix}");
        var issued = await (await victim.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "victim-pc" })).Content.ReadFromJsonAsync<SyncTokenIssuedResponse>();

        (await mallory.GetFromJsonAsync<List<SyncDeviceDto>>("/private-notes/devices/"))
            .Should().BeEmpty();
        (await mallory.DeleteAsync($"/private-notes/devices/{issued!.DeviceId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await mallory.PostAsync($"/private-notes/devices/{issued.DeviceId}/reissue", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 陽性対照: 本人は失効できる
        (await victim.DeleteAsync($"/private-notes/devices/{issued.DeviceId}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    // FR-20 受け入れ基準, FR-22 ③, ADR-0037 決定 18: 期限の 7 日前に通知が 1 回だけ検知される。
    // 期限切れ当日（および期限後）の追加通知は無い。
    [Fact]
    public async Task トークン期限の7日前通知は窓内で1回だけ検知される()
    {
        var user = $"note7-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            // 25 日前に発行 → 期限まで残り 5 日（窓内）
            db.SyncDevices.Add(SyncDevice.Create(user, "expiring", SyncTokens.Generate().Hash,
                now.AddDays(-25)));
            // 10 日前に発行 → 期限まで残り 20 日（窓外・陽性対照の裏）
            db.SyncDevices.Add(SyncDevice.Create(user, "fresh", SyncTokens.Generate().Hash,
                now.AddDays(-10)));
            // 31 日前に発行 → 既に期限切れ（**当日・事後の追加通知は設けない**）
            db.SyncDevices.Add(SyncDevice.Create(user, "expired", SyncTokens.Generate().Hash,
                now.AddDays(-31)));
            await db.SaveChangesAsync();
        }

        await RunMaintenanceAsync(now);
        var first = factory.Notifier.OfKind(PrivateNoteNotificationKinds.SyncTokenExpiry)
            .Where(n => n.Subject == user).ToList();
        first.Should().ContainSingle("窓内の端末だけが 1 通にまとまる");
        first[0].Count.Should().Be(1, "窓外・期限切れの端末は数えない");
        first[0].Deadline.Should().NotBeNull("期限を件数と併せて運ぶ");

        // 再実行しても重複しない
        await RunMaintenanceAsync(now.AddHours(1));
        factory.Notifier.OfKind(PrivateNoteNotificationKinds.SyncTokenExpiry)
            .Where(n => n.Subject == user).Should().HaveCount(1);
    }

    private async Task RunMaintenanceAsync(DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var maintenance = scope.ServiceProvider
            .GetRequiredService<PrivateNoteMaintenanceService>();
        await maintenance.RunAsync(now);
    }
}
