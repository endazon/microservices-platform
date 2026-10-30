using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, NFR-27, SC-17, #2348: システム管理者が、複数の利用者の個人資料の使用量と上限を 1 本でまとめて照会する口
// （`POST /private-notes/quotas/lookup`）。SC-17 の一覧の N+1 を解く。
// 🔴 返すのは利用者ごとの 2 つの数（と割合）だけ。題名・件数・中身は返さない（SC-19 の禁止）。
[Trait("TestKind", "Integration")]
public class PrivateNoteQuotaLookupTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient SessionAs(string user, string? roles = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    // 利用者が同期の口から 1 件の資料（content のバイト数）を作る。
    private async Task PushAsync(string user, string title, string content)
    {
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" },
            TestContext.Current.CancellationToken);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = $"{title}.md", title, edits = new[] { new { content } } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
    }

    private async Task<PrivateNoteUsageDto> SingleAsync(string user) =>
        (await SessionAs("admin").GetFromJsonAsync<PrivateNoteUsageDto>(
            $"/private-notes/quotas/{user}", TestContext.Current.CancellationToken))!;

    [Fact]
    public async Task 複数の利用者の使用量と上限を1本で返し値は1件の口と同じ()
    {
        var alice = NewUser("lookup-a");
        var bob = NewUser("lookup-b");
        var carol = NewUser("lookup-c"); // 資料も上限の行も無い利用者
        await PushAsync(alice, "a1", new string('a', 300));
        await PushAsync(alice, "a2", new string('a', 200));
        await PushAsync(bob, "b1", new string('b', 100));
        (await SessionAs("admin").PutAsJsonAsync($"/private-notes/quotas/{bob}", new { limitBytes = 1000 },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await SessionAs("admin").PostAsJsonAsync("/private-notes/quotas/lookup",
            new { ownerIds = new[] { bob, alice, carol, bob } }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<PrivateNoteQuotaLookupResponse>(TestContext.Current.CancellationToken);
        body!.Items.Select(i => i.OwnerId).Should().Equal([bob, alice, carol], "依頼の順に、重複を除いて返す");
        foreach (var item in body.Items)
        {
            var single = await SingleAsync(item.OwnerId);
            new PrivateNoteUsageDto(item.UsedBytes, item.LimitBytes, item.Percent).Should().Be(single, "1 件の口と同じ値");
        }

        body.Items[0].LimitBytes.Should().Be(1000);
        body.Items[2].Should().Be(new PrivateNoteOwnerUsageDto(carol, 0, 1L * 1024 * 1024 * 1024, 0), "行の無い利用者は既定の 1 GB");
    }

    // 🔴 陰性: 応答の項目は利用者名と 2 つの数と割合だけ（題名・件数・中身・同期設定が無い）。
    [Fact]
    public async Task 応答に題名や件数や中身が無い()
    {
        var user = NewUser("lookup-n");
        await PushAsync(user, "secret-title", "secret-body");

        var res = await SessionAs("admin").PostAsJsonAsync("/private-notes/quotas/lookup",
            new { ownerIds = new[] { user } }, TestContext.Current.CancellationToken);
        var raw = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var item = JsonNode.Parse(raw)!["items"]![0]!.AsObject();
        item.Select(p => p.Key).Should().BeEquivalentTo(["ownerId", "usedBytes", "limitBytes", "percent"]);
        raw.Should().NotContain("secret-title").And.NotContain("secret-body");
    }

    // 二重の守りの後段の側: システム管理者以外は 403。
    [Fact]
    public async Task システム管理者以外は403()
    {
        var res = await SessionAs("someone", roles: "viewer").PostAsJsonAsync("/private-notes/quotas/lookup",
            new { ownerIds = new[] { "alice" } }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public static TheoryData<string[]> InvalidOwnerIds => new()
    {
        Array.Empty<string>(),
        new[] { " " },
        new[] { new string('x', 257) },
        Enumerable.Range(0, 1001).Select(i => $"u{i}").ToArray(),
    };

    [Theory]
    [MemberData(nameof(InvalidOwnerIds))]
    public async Task 空や空白の名前や長すぎる名前や1000件を超える依頼は400(string[] ownerIds)
    {
        var res = await SessionAs("admin").PostAsJsonAsync("/private-notes/quotas/lookup",
            new { ownerIds }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 上限の1000件ちょうどは受け付ける()
    {
        var ownerIds = Enumerable.Range(0, 1000).Select(i => $"edge-{i}").ToArray();

        var res = await SessionAs("admin").PostAsJsonAsync("/private-notes/quotas/lookup",
            new { ownerIds }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<PrivateNoteQuotaLookupResponse>(TestContext.Current.CancellationToken))!
            .Items.Should().HaveCount(1000);
    }
}
