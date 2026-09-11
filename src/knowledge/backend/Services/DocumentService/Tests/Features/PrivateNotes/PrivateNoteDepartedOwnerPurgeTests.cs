using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain.Ports;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Maintenance;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, UC-11, SC-19, SC-10, 計画 ADR-0036 D-09, ADR-0057 決定 1・2, ADR-0096 決定 1・2,
// [[IADR-0296]] 決定 3, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
// **退職して 30 日の閲覧窓が閉じた利用者の個人資料の完全削除。**
//
// 🔴 **陰性対照が本体である。** ADR-0057 決定 2 により残余を置かないため誤削除は取り返せず
// （ADR-0096 §結果）、測るべきは「消えること」より「**消えないこと**」である。
// `WithinWindow` / `NotEvaluable` / 在籍中 / 引けなかった の 4 つを別々の試験で固定する ——
// 束ねると、述語を 1 つ緩めた変異が残りの 3 つで通り抜ける。
[Trait("TestKind", "Integration")]
public class PrivateNoteDepartedOwnerPurgeTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    // 所有者 1 人ぶんの器（同期トークンを発行し、本文つきの資料を作れる状態にする）。
    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync()
    {
        var user = $"depart-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" }, TestContext.Current.CancellationToken);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, session, plugin);
    }

    private async Task<Guid> PushNoteAsync(HttpClient plugin, string path, string content)
    {
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
        {
            vaultPath = path,
            title = path,
            edits = new[] { new { content } },
        }, TestContext.Current.CancellationToken);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await push.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken))!.NoteId;
    }

    private async Task RunMaintenanceAsync(DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrivateNoteMaintenanceService>().RunAsync(now);
    }

    private async Task<bool> NoteExistsAsync(Guid noteId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.PrivateNotes.AnyAsync(n => n.DocumentId == noteId);
    }

    private async Task<bool> DocumentExistsAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return await db.Documents.AnyAsync(d => d.Id == documentId);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    // ── 陽性 ───────────────────────────────────────────────────────────────

    // ADR-0096 決定 1: 射程は ADR-0057 決定 1 と同じ（DB 記録・本文の実体・索引）。
    // 索引はイベントが運ぶので、**3 つすべてを 1 つの試験で測る**（どれか 1 つを落とす変異を通さない）。
    [Fact]
    public async Task 退職して窓が閉じた所有者の資料はDB本文索引の3つとも消える()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "elapsed.md", "退職者の本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareDeparted(user);

        await RunMaintenanceAsync(Now);

        // ① DB 記録（台帳と文書の両方）
        (await NoteExistsAsync(noteId)).Should().BeFalse("ADR-0096 決定 1: 台帳の行が消える");
        (await DocumentExistsAsync(noteId)).Should().BeFalse("ADR-0057 決定 1: 文書の行も消える");

        // ② 本文の実体（オブジェクトストレージ）
        List<string> deleted;
        lock (factory.Storage.Deleted) deleted = [.. factory.Storage.Deleted];
        deleted.Should().NotBeEmpty("ADR-0057 決定 1: 本文の実体まで消える（行だけ消さない）");

        // ③ 索引（DocumentDeleted が下流の掃除を運ぶ）
        factory.Services.GetRequiredService<RecordingMessageBus>()
            .PublishedOf<DocumentDeleted>().Should().Contain(e => e.DocumentId == noteId);
    }

    // ADR-0096 決定 1: 🔴 **監査に載るのは「いつ・誰の・何件」だけ**である。
    // タイトル・本文を載せると、残余を置かないという ADR-0057 決定 2 をログ経由で破る。
    [Fact]
    public async Task 監査には件数と所有者だけが残りタイトルは残らない()
    {
        var (user, _, plugin) = await OwnerAsync();
        const string title = "極秘の議事録タイトル";
        await PushNoteAsync(plugin, $"{title}.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareDeparted(user);

        await RunMaintenanceAsync(Now);

        var entry = factory.Audit.OfAction("private-note.purge.departed")
            .Should().ContainSingle(e => e.Subject == user).Subject;
        entry.Detail.Should().Be("count=1");
        entry.Outcome.Should().Be("granted");
        // 🔴 全項目を走査する（`Detail` だけ見ると、件名へ混ぜる変異を見逃す）。
        string.Join("|", entry.Action, entry.Subject, entry.Outcome, entry.Detail)
            .Should().NotContain(title, "ADR-0096 決定 1: タイトル・本文を監査ログに残さない");
    }

    // ADR-0096 決定 1: 🔴 **FR-22 ① の通知を本経路では送らない**（宛先が無効化済みである）。
    // 90 日の器（①-c 事後通知・容量警告）を巻き込まないことも同時に固定する。
    [Fact]
    public async Task 本経路では通知を1件も送らない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "silent.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareDeparted(user);

        await RunMaintenanceAsync(Now);

        // 陽性対照: 削除自体は起きている（「何も起きなかったから通知も無い」ではない）。
        (await NoteExistsAsync(noteId)).Should().BeFalse();
        factory.Notifier.Notifications.Where(n => n.Subject == user).Should().BeEmpty(
            "ADR-0096 決定 1: 届かない通知を送る設計にしない");
    }

    // ADR-0096 決定 2 / [[IADR-0296]] 決定 3: **失敗は行を残して次周期で再入する。**
    // 「消したことにして実体を残す」形にしないことが決定の要である。
    [Fact]
    public async Task 実体を消せなければ行が残り次周期で消える()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "retry.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareDeparted(user);

        try
        {
            factory.Storage.FailDeleteWhen = _ => true;
            await RunMaintenanceAsync(Now);
            (await NoteExistsAsync(noteId)).Should().BeTrue(
                "実体が消せなかったのだから行を残す（次周期で再入する）");
            (await DocumentExistsAsync(noteId)).Should().BeTrue();
        }
        finally
        {
            factory.Storage.FailDeleteWhen = null;
        }

        // 次周期: 所有者の状態は変わっていないので、同じ述語で再び対象になる。
        await RunMaintenanceAsync(Now.AddDays(1));
        (await NoteExistsAsync(noteId)).Should().BeFalse("次周期で再入して消える");
    }

    // ── 陰性対照 ───────────────────────────────────────────────────────────

    // ADR-0096 決定 1: `WithinWindow`（起点はあるが 30 日はまだ経っていない）は削除しない。
    // 🔴 **変異検出**: 述語を `WithinWindow` 側へ替えるとこの試験が赤になる。
    [Fact]
    public async Task 窓の中の所有者の資料は消えない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "within.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareWithinWindow(user);

        await RunMaintenanceAsync(Now);

        (await NoteExistsAsync(noteId)).Should().BeTrue("D-09 の窓はまだ閉じていない");
        (await DocumentExistsAsync(noteId)).Should().BeTrue();
        factory.Audit.OfAction("private-note.purge.departed").Should().NotContain(e => e.Subject == user);
    }

    // ADR-0096 決定 1: 🔴 **起点が無い／読めない（`NotEvaluable`）は削除しない**（fail-safe）。
    // **人事連携が未配備の間は起点が未供給の利用者が居る**ため、この経路は常用される。
    // 🔴 **変異検出**: `NotEvaluable` の門を外す（`!= WithinWindow` で選ぶ）とこの試験が赤になる。
    [Fact]
    public async Task 起点が未供給または読めない所有者の資料は消えない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "notevaluable.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareNotEvaluable(user);

        await RunMaintenanceAsync(Now);

        (await NoteExistsAsync(noteId)).Should().BeTrue("数えていないものを経過したことにしない");
        (await DocumentExistsAsync(noteId)).Should().BeTrue();
    }

    // ADR-0096 決定 1: **在籍中（`Enabled`）の所有者の資料は消えない。**
    // 🔴 起点の判定が `Elapsed` でも消えない —— `Source=hr-leave-date` の下では
    // 復職者が古い退職日を属性に残したまま有効化され得る（起点は無効化由来ではない）。
    // 🔴 **変異検出**: 述語から `!Enabled` を落とすとこの試験が赤になる。
    [Fact]
    public async Task 在籍中の所有者の資料は窓が閉じていても消えない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "active.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareActive(user, OwnerRetentionEligibility.Elapsed);

        await RunMaintenanceAsync(Now);

        (await NoteExistsAsync(noteId)).Should().BeTrue("無効化されていない利用者は退職していない");
        (await DocumentExistsAsync(noteId)).Should().BeTrue();
    }

    // ADR-0096 決定 1 / [[IADR-0401]]: 🔴 **名簿を引けなかった（`null`）ときは削除しない。**
    // 認可サービスの障害を「窓が閉じた」と読むと、在籍中の利用者の資料が消える。
    // スタブの既定が `null` なので、**何も宣言しないこと**がそのまま「引けなかった」である。
    [Fact]
    public async Task 名簿を引けなかった所有者の資料は消えない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "unavailable.md", "本文");
        factory.Storage.ResetDeletions();

        await RunMaintenanceAsync(Now);

        factory.OwnerRetention.Queried.Should().Contain(user, "所有者は照会されている");
        (await NoteExistsAsync(noteId)).Should().BeTrue("引けなかったことを「退職した」と読まない");
        (await DocumentExistsAsync(noteId)).Should().BeTrue();
    }

    // ADR-0096 決定 1: **名簿に居ない（`Found=false`）も削除の根拠にならない。**
    // 名簿の同期漏れを「退職した」と読むと、在籍中の利用者の資料が消える。
    [Fact]
    public async Task 名簿に居ない所有者の資料は消えない()
    {
        var (user, _, plugin) = await OwnerAsync();
        var noteId = await PushNoteAsync(plugin, "absent.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.Declare(user,
            new OwnerRetentionStatus(
                Found: false, Enabled: false, OwnerRetentionEligibility.Elapsed));

        await RunMaintenanceAsync(Now);

        (await NoteExistsAsync(noteId)).Should().BeTrue("居ないことは退職したことではない");
    }

    // ADR-0096 決定 1: **他人の資料を巻き込まない。** 1 周期に退職者と在籍者が混ざる状況を固定する
    // （所有者ごとに述語を解く形が、まとめて消す形へ退行しないため）。
    [Fact]
    public async Task 同じ周期の在籍者の資料を巻き込まない()
    {
        var (departed, _, departedPlugin) = await OwnerAsync();
        var (active, _, activePlugin) = await OwnerAsync();
        var departedNote = await PushNoteAsync(departedPlugin, "gone.md", "本文");
        var activeNote = await PushNoteAsync(activePlugin, "stay.md", "本文");
        factory.Storage.ResetDeletions();
        factory.OwnerRetention.DeclareDeparted(departed);
        factory.OwnerRetention.DeclareWithinWindow(active);

        await RunMaintenanceAsync(Now);

        (await NoteExistsAsync(departedNote)).Should().BeFalse();
        (await NoteExistsAsync(activeNote)).Should().BeTrue();
    }
}
