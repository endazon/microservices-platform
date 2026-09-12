using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.SyncSettings;

// FR-20, UC-11, SC-20 主要素 3, ADR-0037 決定 3・4, #1442: 同期対象範囲の口。
//
// 🔴 **「外す」は「削除」ではない**（決定 4）。外した資料が消えないことを陽性対照つきで固定する。
[Trait("TestKind", "Integration")]
public class SyncSettingsEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> CreateNoteAsync(HttpClient session, string title, string vaultPath)
    {
        var resp = await session.PostAsJsonAsync("/private-notes/",
            new { title, vaultPath }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PrivateNoteDto>(
            TestContext.Current.CancellationToken))!.Id;
    }

    // ADR-0037 決定 3: **未設定は空配列**（404 ではない）。空＝全資料が対象である。
    [Fact]
    public async Task 未設定の同期設定は空配列で返る()
    {
        var settings = await SessionAs(NewUser("s-empty")).GetFromJsonAsync<SyncSettingsDto>(
            "/private-notes/sync-settings", TestContext.Current.CancellationToken);

        settings!.TargetFolders.Should().BeEmpty();
        settings.UpdatedAt.Should().BeNull("一度も設定していない利用者に更新時刻は無い");
    }

    // SC-20 主要素 3: 保存した値は正規化され、配下の資料数と最終同期が添えられる。
    [Fact]
    public async Task 保存したフォルダは正規化され配下の資料数と最終同期が返る()
    {
        var session = SessionAs(NewUser("s-save"));
        await CreateNoteAsync(session, "配下 1", "work/a.md");
        await CreateNoteAsync(session, "配下 2", "work/深い/b.md");
        await CreateNoteAsync(session, "配下でない", "home/c.md");

        var put = await session.PutAsJsonAsync("/private-notes/sync-settings",
            new UpdateSyncSettingsRequest(["/work/", "home"]), TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = (await put.Content.ReadFromJsonAsync<SyncSettingsDto>(
            TestContext.Current.CancellationToken))!;
        settings.TargetFolders.Select(f => f.Path).Should().Equal("work", "home");
        settings.TargetFolders[0].NoteCount.Should().Be(2, "配下は深さを問わない");
        settings.TargetFolders[1].NoteCount.Should().Be(1);
        settings.TargetFolders[0].LastSyncAt.Should().NotBeNull();
        settings.UpdatedAt.Should().NotBeNull();

        // 取得でも同じ値が返る（保存と読み出しで数え方が変わらない）。
        var fetched = await session.GetFromJsonAsync<SyncSettingsDto>(
            "/private-notes/sync-settings", TestContext.Current.CancellationToken);
        fetched!.TargetFolders.Select(f => f.Path).Should().Equal("work", "home");
    }

    // 🔴 **対象から外しても資料は 1 件も消えない**（ADR-0037 決定 4）。
    // 陽性対照: 外したことで同期状態が `excluded` へ変わる（＝設定は確かに効いている）。
    [Fact]
    public async Task 対象フォルダから外しても資料は削除されない()
    {
        var session = SessionAs(NewUser("s-keep"));
        var id = await CreateNoteAsync(session, "外される資料", "work/keep.md");
        (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await session.PutAsJsonAsync("/private-notes/sync-settings",
            new UpdateSyncSettingsRequest(["home"]), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var notes = (await session.GetFromJsonAsync<PrivateNoteListResponse>(
            "/private-notes/", TestContext.Current.CancellationToken))!.Notes;

        notes.Should().ContainSingle(n => n.Id == id, "同期対象から外すことは削除ではない");
        notes.Single(n => n.Id == id).Deleted.Should().BeFalse();
        notes.Single(n => n.Id == id).SyncState.Should().Be(PrivateNoteSyncStates.Excluded,
            "陽性対照: 設定は効いている（同期が止まっただけである）");
    }

    // 入力違反は 400（RFC7807）。鍵は `targetFolders`。
    [Fact]
    public async Task 不正なフォルダ指定は400になる()
    {
        var session = SessionAs(NewUser("s-400"));

        var resp = await session.PutAsJsonAsync("/private-notes/sync-settings",
            new UpdateSyncSettingsRequest(["work", "/work"]), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("targetFolders");
    }

    // 🔴 **他人の設定は見えない**（ID を受け取る口が無く、主体はトークンからしか採らない）。
    [Fact]
    public async Task 同期設定は主体ごとに分かれている()
    {
        var alice = SessionAs(NewUser("s-alice"));
        var bob = SessionAs(NewUser("s-bob"));

        (await alice.PutAsJsonAsync("/private-notes/sync-settings",
            new UpdateSyncSettingsRequest(["alice-only"]), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var bobSettings = await bob.GetFromJsonAsync<SyncSettingsDto>(
            "/private-notes/sync-settings", TestContext.Current.CancellationToken);
        bobSettings!.TargetFolders.Should().BeEmpty();

        // 陽性対照: alice には自分の設定が見える。
        var aliceSettings = await alice.GetFromJsonAsync<SyncSettingsDto>(
            "/private-notes/sync-settings", TestContext.Current.CancellationToken);
        aliceSettings!.TargetFolders.Select(f => f.Path).Should().Equal("alice-only");
    }
}
