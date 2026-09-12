using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.SyncHistory;

// FR-20, UC-11, SC-20 主要素 6, ADR-0099 決定 2・4・5, #1446（planning#618 の裁定）:
// `GET /private-notes/sync-history` の本人絞り・順序・件数。
//
// **行は DB へ直に置く**（同期を 200 回走らせずに順序と件数を測る）。記録側の正しさは
// `SyncAuditRecorderTests` が端点越しに測っており、ここは**読み側**だけを見る。
[Trait("TestKind", "Integration")]
public class SyncHistoryEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private async Task SeedAsync(string owner, int count, DateTimeOffset from)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.SyncAuditEntries.Add(SyncAuditEntry.Success(owner, Guid.NewGuid(), $"端末{i}",
                SyncDirections.Push, added: 1, updated: 0, deleted: 0, from.AddMinutes(i)));
        }
        await db.SaveChangesAsync(Ct);
    }

    private static string User() => $"hist-{Guid.NewGuid():N}"[..24];

    // ADR-0099 決定 2: **読めるのは本人の記録だけ**（他人の行は 1 件も現れない）＋陽性対照。
    [Fact]
    public async Task 本人の記録だけが返り他人の記録は現れない()
    {
        var mine = User();
        var theirs = User();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(mine, 2, now.AddMinutes(-10));
        await SeedAsync(theirs, 3, now.AddMinutes(-10));

        var entries = await SessionAs(mine)
            .GetFromJsonAsync<List<SyncHistoryEntryDto>>("/private-notes/sync-history", Ct);

        // 陽性対照: 自分の行は見える（「常に空」の実装ではない）。
        entries.Should().HaveCount(2);
        entries!.Should().OnlyContain(e => e.DeviceName.StartsWith("端末"));

        // 陰性: 他人も自分の分だけを見る（同じ器で件数が混ざらない）。
        var theirEntries = await SessionAs(theirs)
            .GetFromJsonAsync<List<SyncHistoryEntryDto>>("/private-notes/sync-history", Ct);
        theirEntries.Should().HaveCount(3);
    }

    // ADR-0099 決定 4: **新しい順**（画面は直近を上に出す）。
    [Fact]
    public async Task 実行日時の新しい順に返る()
    {
        var user = User();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedAsync(user, 5, start);

        var entries = await SessionAs(user)
            .GetFromJsonAsync<List<SyncHistoryEntryDto>>("/private-notes/sync-history", Ct);

        entries.Should().HaveCount(5);
        entries!.Select(e => e.OccurredAt).Should().BeInDescendingOrder();
        entries[0].DeviceName.Should().Be("端末4", "最後に記録した行が先頭に来る");
    }

    // ADR-0099 決定 4: 既定は 50 件（保持〔3 年〕とは別の値である）。
    [Fact]
    public async Task 既定は50件で打ち切られる()
    {
        var user = User();
        await SeedAsync(user, 60, DateTimeOffset.UtcNow.AddHours(-2));

        var entries = await SessionAs(user)
            .GetFromJsonAsync<List<SyncHistoryEntryDto>>("/private-notes/sync-history", Ct);

        entries.Should().HaveCount(50);
        // 打ち切られるのは**古い側**である（新しい順に 50 件）。
        entries!.Should().NotContain(e => e.DeviceName == "端末0");
        entries.Should().Contain(e => e.DeviceName == "端末59");
    }

    // `limit` は 1〜200。**範囲外は黙って丸めず 400 である**（画面との食い違いを作らない）。
    [Fact]
    public async Task limitは1から200で範囲外は400になる()
    {
        var user = User();
        await SeedAsync(user, 5, DateTimeOffset.UtcNow.AddHours(-3));
        var session = SessionAs(user);

        // 陽性対照: 範囲内は通り、指定した件数で打ち切られる。
        var three = await session.GetFromJsonAsync<List<SyncHistoryEntryDto>>(
            "/private-notes/sync-history?limit=3", Ct);
        three.Should().HaveCount(3);

        (await session.GetAsync("/private-notes/sync-history?limit=0", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await session.GetAsync("/private-notes/sync-history?limit=201", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 🔴 ADR-0099 決定 5: 応答に題名もパスも資料 ID も現れない（DTO にも表にも無い）。
    [Fact]
    public async Task 応答に題名もパスも資料IDも現れない()
    {
        var user = User();
        await SeedAsync(user, 1, DateTimeOffset.UtcNow.AddMinutes(-5));

        var json = await SessionAs(user).GetStringAsync("/private-notes/sync-history", Ct);

        json.Should().NotBeEmpty("陽性対照: 応答そのものは返っている");
        json.Should().NotContain("title").And.NotContain("vaultPath")
            .And.NotContain("noteId").And.NotContain("documentId")
            .And.NotContain("deviceId", "端末は名前だけを出す（識別子は出さない）");
    }

    // 無認証は 401（群の `RequireAuthorization()` が在ることの検査）。
    [Fact]
    public async Task 主体を持たない要求は401になる()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");

        (await client.GetAsync("/private-notes/sync-history", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
