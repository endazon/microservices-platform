using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.ObsidianSync;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099, planning#618, #1446:
// 同期の実行が**貯蔵つきの監査ログ**（`SyncAuditEntry`）へ残ることを固定する。
//
// 🔴 否定形（残らない・列が無い）は**陽性対照と対で置く** ——
// 「1 行も書かない実装」でも否定形だけなら緑になる。
//
// 🔴 **応答（状態・本文）が変わっていないことも同じテストで測る。** 記録を足した結果として
// プロトコルが変わるのが最悪であり、`ObsidianSyncProtocolTests` と
// `SyncValidationProblemContractTests` が固定している形をここでも崩さない。
[Trait("TestKind", "Integration")]
public class SyncAuditRecorderTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync(
        long? limitBytes = null)
    {
        var user = $"audit-{Guid.NewGuid():N}"[..24];
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "自宅 PC" }, Ct);
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;

        if (limitBytes is { } limit)
        {
            (await SessionAs("admin").PutAsJsonAsync($"/private-notes/quotas/{user}",
                new { limitBytes = limit }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return (user, session, PluginWith(token));
    }

    private static object PushBody(string title, string path, string content) => new
    {
        vaultPath = path,
        title,
        edits = new[] { new { content } },
    };

    private async Task<int> TotalEntriesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.SyncAuditEntries.CountAsync(Ct);
    }

    private async Task<List<SyncAuditEntry>> EntriesOfAsync(string owner)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.SyncAuditEntries.Where(a => a.OwnerId == owner)
            .OrderBy(a => a.OccurredAt).ToListAsync(Ct);
    }

    // ── 成功の 5 経路（内訳が経路ごとに違う）────────────────────────────

    // ADR-0099 決定 5: push の新規作成は `added=1`、更新は `updated=1`。
    [Fact]
    public async Task pushの新規作成と更新は追加1件と更新1件として残る()
    {
        var (user, _, plugin) = await OwnerAsync();

        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("メモ", "notes/memo.md", "初版"), Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);

        var newEntry = (await EntriesOfAsync(user)).Should().ContainSingle().Subject;
        newEntry.Direction.Should().Be(SyncDirections.Push);
        newEntry.Outcome.Should().Be(SyncOutcomes.Success);
        newEntry.Added.Should().Be(1);
        newEntry.Updated.Should().Be(0);
        newEntry.Deleted.Should().Be(0);
        newEntry.Conflicted.Should().Be(0);
        newEntry.FailureReason.Should().BeNull();
        newEntry.DeviceName.Should().Be("自宅 PC", "端末名は記録時点の写しである（決定 5）");

        var updated = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note!.NoteId,
            vaultPath = "notes/memo.md",
            title = "メモ",
            baseVersion = note.Version,
            edits = new[] { new { content = "二版" } },
        }, Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await EntriesOfAsync(user);
        entries.Should().HaveCount(2);
        entries[1].Added.Should().Be(0);
        entries[1].Updated.Should().Be(1);
        entries[1].Outcome.Should().Be(SyncOutcomes.Success);
    }

    // ADR-0099 決定 5: pull は `direction=pull` かつ `updated=1`
    // （端末が受け取った 1 件。追加か更新かはサーバが知らない）。
    [Fact]
    public async Task pullは受信方向の更新1件として残る()
    {
        var (user, _, plugin) = await OwnerAsync();
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("受信", "pull.md", "本文"), Ct);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);

        (await plugin.GetAsync($"/private-notes/sync/notes/{note!.NoteId}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await EntriesOfAsync(user);
        entries.Should().HaveCount(2);
        entries[1].Direction.Should().Be(SyncDirections.Pull);
        entries[1].Updated.Should().Be(1);
        entries[1].Outcome.Should().Be(SyncOutcomes.Success);
    }

    // ADR-0099 決定 5: 同期経由の削除は `deleted=1`（方向は端末 → サーバ＝push）。
    [Fact]
    public async Task 同期経由の削除は削除1件として残る()
    {
        var (user, _, plugin) = await OwnerAsync();
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("消す", "del.md", "本文"), Ct);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);

        (await plugin.PostAsJsonAsync($"/private-notes/sync/notes/{note!.NoteId}/delete",
            new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await EntriesOfAsync(user);
        entries.Should().HaveCount(2);
        entries[1].Deleted.Should().Be(1);
        entries[1].Direction.Should().Be(SyncDirections.Push);
        entries[1].Outcome.Should().Be(SyncOutcomes.Success);
    }

    // ADR-0099 決定 5: リネーム（move）は `updated=1`。
    [Fact]
    public async Task リネームは更新1件として残る()
    {
        var (user, _, plugin) = await OwnerAsync();
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("名前", "before.md", "本文"), Ct);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);

        var move = await plugin.PostAsJsonAsync($"/private-notes/sync/notes/{note!.NoteId}/move",
            new { vaultPath = "after.md", version = note.Version }, Ct);
        move.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await EntriesOfAsync(user);
        entries.Should().HaveCount(2);
        entries[1].Updated.Should().Be(1);
        entries[1].Outcome.Should().Be(SyncOutcomes.Success);
    }

    // ── 失敗の 7 理由（ADR-0099 決定 6）──────────────────────────────

    // 🔴 **競合の記録（`SyncConflict`）と同期履歴の失敗（`SyncAuditEntry`）は別の行である。**
    // 409 の応答（状態・本文）は 1 バイトも変わらない。
    [Fact]
    public async Task 版不一致は競合台帳と同期履歴の両方に残り409の本文は変わらない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("競合", "conflict.md", "初版"), Ct);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);

        // v1 → v2（成功）
        var first = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note!.NoteId,
            vaultPath = "conflict.md",
            title = "競合",
            baseVersion = note.Version,
            edits = new[] { new { content = "端末Aの編集" } },
        }, Ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // 古い版を土台にした push → 409
        var stale = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note.NoteId,
            vaultPath = "conflict.md",
            title = "競合",
            baseVersion = note.Version,
            edits = new[] { new { content = "端末Bの編集" } },
        }, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await stale.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("version_conflict").And.Contain("serverVersion")
            .And.Contain("serverUpdatedAt", "409 の本文は記録を足しても変えない");

        var failure = (await EntriesOfAsync(user)).Last();
        failure.Outcome.Should().Be(SyncOutcomes.Failure);
        failure.FailureReason.Should().Be(SyncFailureReasons.VersionConflict);
        failure.Conflicted.Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var conflicts = await db.SyncConflicts.Where(c => c.OwnerId == user).ToListAsync(Ct);
        conflicts.Should().ContainSingle("競合台帳の行は同期履歴の行と別に立つ");
    }

    // 409（削除済みへの push）・409（パス衝突）・507（容量）・413（本文上限）・400（検証）・404（不在）。
    [Fact]
    public async Task 削除済みへのpushはdeletedとして残る()
    {
        var (user, _, plugin) = await OwnerAsync();
        var created = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("削除済み", "gone.md", "本文"), Ct);
        var note = await created.Content.ReadFromJsonAsync<PushNoteResponse>(Ct);
        (await plugin.PostAsJsonAsync($"/private-notes/sync/notes/{note!.NoteId}/delete",
            new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = note.NoteId,
            vaultPath = "gone.md",
            title = "削除済み",
            baseVersion = note.Version,
            edits = new[] { new { content = "書き戻し" } },
        }, Ct);
        push.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await push.Content.ReadAsStringAsync(Ct)).Should().Contain("deleted");

        var last = (await EntriesOfAsync(user)).Last();
        last.FailureReason.Should().Be(SyncFailureReasons.Deleted);
        last.Outcome.Should().Be(SyncOutcomes.Failure);
    }

    [Fact]
    public async Task パス衝突はpath_conflictとして残る()
    {
        var (user, _, plugin) = await OwnerAsync();
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("先着", "same.md", "本文"), Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var dup = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("後着", "same.md", "本文"), Ct);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await dup.Content.ReadAsStringAsync(Ct)).Should().Contain("vault_path_conflict");

        (await EntriesOfAsync(user)).Last().FailureReason
            .Should().Be(SyncFailureReasons.PathConflict);
    }

    [Fact]
    public async Task 容量超過はquota_exceededとして残る()
    {
        var (user, _, plugin) = await OwnerAsync(limitBytes: 1_000);
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("満杯", "full.md", new string('f', 1000)), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var over = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("あふれ", "over.md", "x"), Ct);
        over.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
        (await over.Content.ReadAsStringAsync(Ct))
            .Should().Contain("論理削除では容量は空きません", "507 の本文は記録を足しても変えない");

        (await EntriesOfAsync(user)).Last().FailureReason
            .Should().Be(SyncFailureReasons.QuotaExceeded);
    }

    [Fact]
    public async Task 本文上限超過はbody_too_largeとして残る()
    {
        var (user, _, plugin) = await OwnerAsync();

        var tooLarge = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("大きい", "big.md", new string('あ', 350_000)), Ct);
        tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        (await EntriesOfAsync(user)).Should().ContainSingle()
            .Which.FailureReason.Should().Be(SyncFailureReasons.BodyTooLarge);
    }

    [Fact]
    public async Task 検証エラーはinvalid_requestとして残る()
    {
        var (user, _, plugin) = await OwnerAsync();

        var invalid = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = "", title = "", edits = Array.Empty<object>() }, Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await EntriesOfAsync(user)).Should().ContainSingle()
            .Which.FailureReason.Should().Be(SyncFailureReasons.InvalidRequest);
    }

    [Fact]
    public async Task 不在の資料へのpushはnot_foundとして残る()
    {
        var (user, _, plugin) = await OwnerAsync();

        var ghost = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            noteId = Guid.NewGuid(),
            vaultPath = "ghost.md",
            title = "不在",
            baseVersion = 1,
            edits = new[] { new { content = "本文" } },
        }, Ct);
        ghost.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await EntriesOfAsync(user)).Should().ContainSingle()
            .Which.FailureReason.Should().Be(SyncFailureReasons.NotFound);
    }

    // ── 🔴 401 は記録しない（所有者が決まらない）＋陽性対照 ──────────────────

    [Fact]
    public async Task 認証できない同期は記録されないが正しいトークンでは記録される()
    {
        var (user, _, plugin) = await OwnerAsync();
        // 🔴 **全件の件数で測る**（0 件ではない）—— 本クラスは器を共有するので、
        // 他の試験が書いた行が同じ DB に居る。**「増えないこと」が固定したい性質である。**
        var before = await TotalEntriesAsync();

        // 否定形: 出鱈目なトークン・トークン無しはどちらも 401 で、行は 1 つも増えない。
        (await PluginWith("deadbeef").PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("匿名", "anon.md", "本文"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateClient().PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("匿名", "anon.md", "本文"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await TotalEntriesAsync()).Should().Be(before,
            "401 は所有者が決まらないので 1 行も記録しない");

        // 🔴 陽性対照: 同じ要求が正しいトークンでは記録される（「常に記録しない」実装ではない）。
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody("匿名", "anon.md", "本文"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await EntriesOfAsync(user)).Should().ContainSingle();
    }

    // ── 🔴 ADR-0099 決定 5: 題名・パス・資料 ID を運ぶ列が「存在しない」 ─────────
    //
    // **規約ではなく型で守る。** 列を足した人がここで赤くなる。
    [Fact]
    public void 同期履歴の行は資料を指す列を持たない()
    {
        var names = typeof(SyncAuditEntry).GetProperties().Select(p => p.Name).ToList();

        // 陽性対照を先に置く: 決定 5 が要求する列は在る。
        names.Should().Contain(nameof(SyncAuditEntry.OccurredAt))
            .And.Contain(nameof(SyncAuditEntry.DeviceName))
            .And.Contain(nameof(SyncAuditEntry.Direction))
            .And.Contain(nameof(SyncAuditEntry.Outcome));

        // 本題: 資料を指す列・題名・パスは 1 つも無い。
        names.Should().NotContain("DocumentId").And.NotContain("NoteId")
            .And.NotContain("Title").And.NotContain("VaultPath").And.NotContain("Path");
    }

    // 🔴 監査ログ（構造化ログ側）にも題名・パスを書かない（ADR-0037 決定 9・決定 3）。
    [Fact]
    public async Task 監査ログの詳細に題名もパスも現れない()
    {
        var (user, _, plugin) = await OwnerAsync();
        const string secretTitle = "監査に漏れてはならない題名";
        const string secretPath = "secret/folder/leak.md";

        (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            PushBody(secretTitle, secretPath, "秘密の本文"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var records = factory.Audit.Entries.Where(e => e.Subject == user).ToList();
        records.Should().NotBeEmpty("陽性対照: 記録そのものは出ている");
        foreach (var entry in records)
        {
            var detail = entry.Detail ?? string.Empty;
            detail.Should().NotContain(secretTitle).And.NotContain(secretPath)
                .And.NotContain("秘密の本文");
        }
        // 内訳・方向は detail に載る（運用が同じ 1 行で追える）。
        records.Should().Contain(e => (e.Detail ?? string.Empty).Contains("direction=push")
                                   && (e.Detail ?? string.Empty).Contains("added=1"));
    }
}
