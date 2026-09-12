using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, FR-20, UC-11, SC-19 主要素 1・2・5, ADR-0036 D-06, ADR-0037 決定 3・4・7, ADR-0063, #1441:
// 一覧が返す**導出項目**（公開範囲・共有件数・同期状態・タグ名）を固定する。
//
// 🔴 **3 状態はどれも「他の 2 つが出ない」ことまで測る。** 片方だけ測ると
// 「常に `private`」「常に `excluded`」を返す実装が緑を通る（陰性と陽性が同じ試験の中で対になる）。
[Trait("TestKind", "Integration")]
public class PrivateNoteListDerivationTests(TestWebApplicationFactory factory)
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

    private async Task<Dictionary<Guid, PrivateNoteDto>> ListAsync(HttpClient session)
    {
        var body = await session.GetFromJsonAsync<PrivateNoteListResponse>(
            "/private-notes/", TestContext.Current.CancellationToken);
        return body!.Notes.ToDictionary(n => n.Id);
    }

    private async Task WithDbAsync(Func<DocumentDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    // ── SC-19 主要素 2, ADR-0036 D-06: 公開範囲の 3 状態 ──────────────────
    //
    // 🔴 **3 つを 1 つの試験で対にする。** グループ共有は個人共有より広いので、
    // 両方あるときは `groups` である（片方ずつ測ると優先順位が測れない）。
    [Fact]
    public async Task 公開範囲は共有台帳から3状態に導出される()
    {
        var user = NewUser("vis");
        var session = SessionAs(user);

        var alone = await CreateNoteAsync(session, "非公開メモ", "vis/alone.md");
        var shared = await CreateNoteAsync(session, "個人共有メモ", "vis/shared.md");
        var grouped = await CreateNoteAsync(session, "グループ共有メモ", "vis/grouped.md");

        (await session.PostAsJsonAsync($"/documents/{shared}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await session.PostAsJsonAsync($"/documents/{grouped}/shares",
            new { subjectType = "user", subjectId = "carol" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await session.PostAsJsonAsync($"/documents/{grouped}/shares",
            new { subjectType = "group", subjectId = "sales" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var notes = await ListAsync(session);

        notes[alone].Visibility.Should().Be(PrivateNoteVisibilityValues.Private, "共有 0 行は非公開");
        notes[alone].SharedUserCount.Should().Be(0);
        notes[alone].SharedGroupCount.Should().Be(0);

        notes[shared].Visibility.Should().Be(PrivateNoteVisibilityValues.Users, "user 行だけなら個人指定");
        notes[shared].SharedUserCount.Should().Be(1);
        notes[shared].SharedGroupCount.Should().Be(0);

        notes[grouped].Visibility.Should().Be(PrivateNoteVisibilityValues.Groups,
            "group 行が 1 つでもあればグループ指定（個人共有より広い状態を採る）");
        notes[grouped].SharedUserCount.Should().Be(1);
        notes[grouped].SharedGroupCount.Should().Be(1);
    }

    // ── SC-19 主要素 5, ADR-0037 決定 3: 有効な同期端末が無ければ対象外 ─────────
    //
    // 陽性対照は次の試験（端末を発行すれば同じ資料が `target` になる）。
    [Fact]
    public async Task 有効な同期端末が無い所有者の資料は同期対象外になる()
    {
        var session = SessionAs(NewUser("nodev"));
        var id = await CreateNoteAsync(session, "端末なし", "nodev/memo.md");

        (await ListAsync(session))[id].SyncState.Should().Be(PrivateNoteSyncStates.Excluded,
            "同期する端末が 1 台も無ければ、その資料は同期されない");
    }

    // ADR-0037 決定 3・4: 端末があり、対象フォルダが未設定なら**全資料が対象**である。
    // 対象フォルダを設定すると、配下だけが `target` になる（外は `excluded`）。
    [Fact]
    public async Task 同期対象フォルダの設定で対象と対象外が分かれる()
    {
        var user = NewUser("folder");
        var session = SessionAs(user);
        var inside = await CreateNoteAsync(session, "配下", "work/notes/inside.md");
        var outside = await CreateNoteAsync(session, "配下でない", "private/outside.md");
        // 前方一致の罠: `work` を対象にしても `work-old/...` は配下ではない。
        var lookalike = await CreateNoteAsync(session, "似た名前", "work-old/lookalike.md");

        (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // フォルダ未設定（既定）: 3 件とも対象である。
        var beforeSettings = await ListAsync(session);
        beforeSettings[inside].SyncState.Should().Be(PrivateNoteSyncStates.Target);
        beforeSettings[outside].SyncState.Should().Be(PrivateNoteSyncStates.Target,
            "対象フォルダ未設定は「全資料が対象」である（空集合＝対象なしではない）");
        beforeSettings[lookalike].SyncState.Should().Be(PrivateNoteSyncStates.Target);

        (await session.PutAsJsonAsync("/private-notes/sync-settings/",
            new UpdateSyncSettingsRequest(["work"]), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var afterSettings = await ListAsync(session);
        afterSettings[inside].SyncState.Should().Be(PrivateNoteSyncStates.Target);
        afterSettings[outside].SyncState.Should().Be(PrivateNoteSyncStates.Excluded);
        afterSettings[lookalike].SyncState.Should().Be(PrivateNoteSyncStates.Excluded,
            "`work` の配下は `work/` で始まる資料だけである（`work-old/` は別のフォルダ）");
    }

    // ADR-0037 決定 7: 未解決の競合がある資料は `conflict`（対象／対象外より強い）。
    // 削除済みの資料は常に `excluded`（陽性対照は同じ試験内の未削除の資料）。
    [Fact]
    public async Task 未解決の競合は同期状態を上書きし削除済みは対象外になる()
    {
        var user = NewUser("conf");
        var session = SessionAs(user);
        var conflicted = await CreateNoteAsync(session, "競合あり", "conf/a.md");
        var clean = await CreateNoteAsync(session, "競合なし", "conf/b.md");
        var deleted = await CreateNoteAsync(session, "削除済み", "conf/c.md");

        (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await WithDbAsync(db =>
        {
            db.SyncConflicts.Add(SyncConflict.Detect(conflicted, user, Guid.NewGuid(),
                localBaseVersion: 1, serverVersion: 2, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });
        (await session.DeleteAsync($"/private-notes/{deleted}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var notes = await ListAsync(session);
        notes[conflicted].SyncState.Should().Be(PrivateNoteSyncStates.Conflict);
        notes[clean].SyncState.Should().Be(PrivateNoteSyncStates.Target,
            "陽性対照: 同じ所有者の競合していない資料は対象のままである");
        notes[deleted].SyncState.Should().Be(PrivateNoteSyncStates.Excluded,
            "削除済みの資料は同期の対象にならない");
    }

    // ── SC-19 主要素 1, ADR-0063: タグは**辞書の表示名**で返る ────────────────
    //
    // 🔴 **辞書に無い ID は落とす**（`TagResolver.ToNames` の既存の規則）。
    // 陽性対照（辞書に在る ID は表示名で出る）と同じ試験で対にする。
    [Fact]
    public async Task タグは辞書の表示名で返り辞書に無いIDは落ちる()
    {
        var session = SessionAs(NewUser("tag"));
        var id = await CreateNoteAsync(session, "タグ付き", "tag/memo.md");

        // 辞書の表示名は一意なので、テストごとに別名を使う（器はクラス内で共有される）。
        var tagName = $"営業{Guid.NewGuid():N}"[..10];
        var unknown = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            var tag = Tag.Create(tagName);
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            var doc = await db.Documents.FirstAsync(d => d.Id == id);
            doc.AddTag(tag.Id);
            doc.AddTag(unknown);
        });

        var note = (await ListAsync(session))[id];
        note.Tags.Should().Equal(tagName);
        note.Tags.Should().NotContain(unknown.ToString(), "辞書に無い ID は表示名にできないので落とす");
    }

    // 🔴 **一覧以外の口も同じ導出で埋める**（作成・復元・露出更新）。
    // 口ごとに別の埋め方をすると、画面が「作成直後だけ非公開に見える」ような差を拾う。
    [Fact]
    public async Task 作成と復元と露出更新の応答も同じ導出で埋まる()
    {
        var user = NewUser("same");
        var session = SessionAs(user);
        (await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await session.PostAsJsonAsync("/private-notes/",
            new { title = "同じ導出", vaultPath = "same/memo.md" },
            TestContext.Current.CancellationToken);
        var note = (await created.Content.ReadFromJsonAsync<PrivateNoteDto>(
            TestContext.Current.CancellationToken))!;
        note.Visibility.Should().Be(PrivateNoteVisibilityValues.Private);
        note.SyncState.Should().Be(PrivateNoteSyncStates.Target, "端末があり対象フォルダは未設定である");
        note.Tags.Should().BeEmpty();

        var exposure = await session.PutAsJsonAsync($"/private-notes/{note.Id}/exposure",
            new UpdateExposureRequest(true, false, false), TestContext.Current.CancellationToken);
        (await exposure.Content.ReadFromJsonAsync<PrivateNoteDto>(
            TestContext.Current.CancellationToken))!.SyncState
            .Should().Be(PrivateNoteSyncStates.Target, "露出 3 トグルと同期状態は別物である");

        (await session.DeleteAsync($"/private-notes/{note.Id}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = await session.PostAsync($"/private-notes/{note.Id}/restore", null,
            TestContext.Current.CancellationToken);
        (await restored.Content.ReadFromJsonAsync<PrivateNoteDto>(
            TestContext.Current.CancellationToken))!.SyncState
            .Should().Be(PrivateNoteSyncStates.Target, "復元したら「削除済みだから対象外」ではなくなる");
    }
}
