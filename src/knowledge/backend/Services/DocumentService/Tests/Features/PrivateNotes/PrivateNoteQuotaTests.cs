using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync;
using DocumentService.Domain.Ports;
// #451-a: 個人資料の応答 DTO は Knowledge.Contracts へ集約した（BFF と定義を 1 つにするため）。
using Knowledge.Contracts.Dtos;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, NFR-27, FR-22, ADR-0037 決定 16・17・19・20, [[IADR-0270]] 決定 4:
// 保存容量の算入範囲（最新版＋論理削除済み／版履歴は非算入）・80/95 警告・
// 100% 到達時の「新規作成のみ拒否・更新は許す」・完全削除による自力復帰。
[Trait("TestKind", "Integration")]
public class PrivateNoteQuotaTests(TestWebApplicationFactory factory)
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

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync(
        long limitBytes)
    {
        var user = $"quota-{Guid.NewGuid():N}"[..24];
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices",
            new { deviceName = "pc" });
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>())!.Token;
        // 管理者が上限を設定する（テストしやすい小さな値へ）
        var admin = SessionAs("admin");
        (await admin.PutAsJsonAsync($"/private-notes/quotas/{user}",
            new { limitBytes })).StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, session, PluginWith(token));
    }

    private static object Push(string title, string path, string content) => new
    {
        vaultPath = path,
        title,
        edits = new[] { new { content } },
    };

    private static object Update(Guid noteId, string path, int baseVersion, string content) => new
    {
        noteId,
        vaultPath = path,
        title = "更新",
        baseVersion,
        edits = new[] { new { content } },
    };

    private async Task<PrivateNoteUsageDto> UsageOf(HttpClient session)
        => (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/"))!.Usage;

    // FR-19 受け入れ基準 ①: 同一資料を繰り返し編集しても、使用量は最新版 1 件分から増えない。
    [Fact]
    public async Task 版履歴は使用量に算入されない()
    {
        var (_, session, plugin) = await OwnerAsync(limitBytes: 100_000);
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("履歴", "hist.md", new string('a', 400)), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
        {
            var current = await plugin.GetFromJsonAsync<PullNoteResponse>(
                $"/private-notes/sync/notes/{note!.NoteId}", TestContext.Current.CancellationToken);
            var update = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
                Update(note.NoteId, "hist.md", current!.Version, new string('b', 400)), TestContext.Current.CancellationToken);
            update.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var usage = await UsageOf(session);
        usage.UsedBytes.Should().Be(400, "算入されるのは最新版のみ（版履歴は算入しない）");
    }

    // FR-19 受け入れ基準 ②, ADR-0037 決定 19・20: 論理削除では使用量が減らず、
    // 完全削除した時点で当該資料の分だけ減る。
    [Fact]
    public async Task 論理削除では使用量が減らず完全削除で減る()
    {
        var (_, session, plugin) = await OwnerAsync(limitBytes: 100_000);
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("削除", "del.md", new string('x', 500)), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        (await UsageOf(session)).UsedBytes.Should().Be(500);

        // 論理削除 —— 使用量は減らない（算入され続ける）
        var del = await session.DeleteAsync($"/private-notes/{note!.NoteId}", TestContext.Current.CancellationToken);
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UsageOf(session)).UsedBytes.Should().Be(500,
            "論理削除済み（90 日の保管中）も容量に算入する");

        // 完全削除 —— 解放される容量が応答に載り、使用量が減る
        var purge = await session.PostAsJsonAsync("/private-notes/purge",
            new { ids = new[] { note.NoteId } }, TestContext.Current.CancellationToken);
        purge.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await purge.Content.ReadFromJsonAsync<PurgePrivateNotesResponse>(TestContext.Current.CancellationToken);
        result!.FreedBytes.Should().Be(500, "解放される容量を実行時に示す（決定 20）");
        (await UsageOf(session)).UsedBytes.Should().Be(0);
    }

    // FR-19 受け入れ基準 ⑤⑥, ADR-0037 決定 17: 100% で新規作成は拒否・既存資料の更新は成功する。
    // 完全削除で 100% を下回ると新規作成が再び成功する（自力復帰）。
    [Fact]
    public async Task 満杯時は新規作成のみ拒否され更新は通り完全削除で復帰する()
    {
        var (_, session, plugin) = await OwnerAsync(limitBytes: 1_000);

        // ちょうど上限まで使う（上限ちょうどの新規作成は跨がないため許される）
        var fill = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("満杯", "full.md", new string('f', 1000)), TestContext.Current.CancellationToken);
        fill.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await fill.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);

        // 新規作成（同期経由）→ 拒否
        var create = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("あふれ", "over.md", "x"), TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);

        // 新規作成（SC-19 経由・本文なし 0 バイト）→ これも拒否（100% に達している）
        var createUi = await session.PostAsJsonAsync("/private-notes/",
            new { title = "空メモ" }, TestContext.Current.CancellationToken);
        createUi.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);

        // 既存資料の更新 → **成功する**（書きかけを失わせない。上限を超えて増えてよい）
        var update = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note!.NoteId, "full.md", note.Version, new string('g', 1200)), TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK,
            "100% でも既存資料の更新（保存）は成功する");
        (await UsageOf(session)).UsedBytes.Should().Be(1200);

        // 完全削除で 100% を下回ると新規作成が再び成功する
        (await session.DeleteAsync($"/private-notes/{note.NoteId}", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.PostAsJsonAsync("/private-notes/purge",
            new { ids = new[] { note.NoteId } }, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var retry = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("復帰", "again.md", "y"), TestContext.Current.CancellationToken);
        retry.StatusCode.Should().Be(HttpStatusCode.Created,
            "管理者へ依頼せず利用者が自力で復帰できる");
    }

    // [[IADR-0270]] 決定 4: 上限を跨ぐ新規作成も拒否する（超過は更新の増分に限る）。
    [Fact]
    public async Task 上限を跨ぐ新規作成は拒否され跨がない新規作成は通る()
    {
        var (_, _, plugin) = await OwnerAsync(limitBytes: 1_000);

        var half = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("半分", "half.md", new string('h', 500)), TestContext.Current.CancellationToken);
        half.StatusCode.Should().Be(HttpStatusCode.Created);

        // 500 + 600 > 1000 → 拒否
        var over = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("超過", "over.md", new string('o', 600)), TestContext.Current.CancellationToken);
        over.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);

        // 500 + 500 = 1000（跨がない）→ 許可（陽性対照）
        var exact = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("丁度", "exact.md", new string('e', 500)), TestContext.Current.CancellationToken);
        exact.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // FR-19 受け入れ基準 ④, FR-22 ②, ADR-0037 決定 17: 80% / 95% の跨ぎで各 1 回警告が届き、
    // 閾値を下回ると再武装する。
    [Fact]
    public async Task 容量警告は80と95の跨ぎで各1回発火し下回ると再武装する()
    {
        var (user, session, plugin) = await OwnerAsync(limitBytes: 1_000);
        List<RecordingPrivateNoteNotifier.Sent> Warnings() =>
            factory.Notifier.OfKind(PrivateNoteNotificationKinds.StorageQuotaWarning)
                .Where(n => n.Subject == user).ToList();

        // 70% — 警告なし
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Push("警告", "warn.md", new string('a', 700)), TestContext.Current.CancellationToken);
        var note = await push.Content.ReadFromJsonAsync<PushNoteResponse>(TestContext.Current.CancellationToken);
        Warnings().Should().BeEmpty("80% 未満では警告しない");

        // 85% — 80% の警告が 1 回
        var current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note!.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('b', 850)), TestContext.Current.CancellationToken);
        Warnings().Should().ContainSingle().Which.ThresholdPercent.Should().Be(80);

        // 87% — 既に警告済みの閾値では再警告しない
        current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('c', 870)), TestContext.Current.CancellationToken);
        Warnings().Should().HaveCount(1, "同一閾値の再通知はしない");

        // 96% — 95% の警告が 1 回
        current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('d', 960)), TestContext.Current.CancellationToken);
        Warnings().Should().HaveCount(2);
        Warnings()[1].ThresholdPercent.Should().Be(95);

        // 97% — 95% 以上に留まる間は再警告しない（変異試験で検出漏れが実測された跨ぎの対照）
        current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('g', 970)), TestContext.Current.CancellationToken);
        Warnings().Should().HaveCount(2, "95% の警告も跨ぎで 1 回だけ");

        // 縮小して閾値を下回る（30%）→ 再武装 → 再度 85% で 80% 警告がもう 1 回
        current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('e', 300)), TestContext.Current.CancellationToken);
        current = await plugin.GetFromJsonAsync<PullNoteResponse>(
            $"/private-notes/sync/notes/{note.NoteId}", TestContext.Current.CancellationToken);
        await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            Update(note.NoteId, "warn.md", current!.Version, new string('f', 850)), TestContext.Current.CancellationToken);
        Warnings().Should().HaveCount(3, "閾値を下回った後の再跨ぎでは改めて警告する");
        Warnings()[2].ThresholdPercent.Should().Be(80);

        // FR-22: 宛先は所有者本人のみ
        Warnings().Should().OnlyContain(n => n.Subject == user);
    }

    // FR-19, NFR-27: 管理者による上限変更は 1 TB まで。範囲外は 400。
    [Fact]
    public async Task 上限は管理者が変更でき1TBを超える値は拒否される()
    {
        var user = $"limit-{Guid.NewGuid():N}"[..24];
        var admin = SessionAs("admin");

        var ok = await admin.PutAsJsonAsync($"/private-notes/quotas/{user}",
            new { limitBytes = 1024L * 1024 * 1024 * 1024 }, TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "1 TB ちょうどは許容される");

        var tooBig = await admin.PutAsJsonAsync($"/private-notes/quotas/{user}",
            new { limitBytes = 1024L * 1024 * 1024 * 1024 + 1 }, TestContext.Current.CancellationToken);
        tooBig.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var zero = await admin.PutAsJsonAsync($"/private-notes/quotas/{user}",
            new { limitBytes = 0 }, TestContext.Current.CancellationToken);
        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 非管理者は変更できない（統制の向き）
        var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        var denied = await viewer.PutAsJsonAsync($"/private-notes/quotas/{user}",
            new { limitBytes = 1000 }, TestContext.Current.CancellationToken);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-19: 既定の上限は 1 GB（quota 行を作っていない利用者にも適用される）。
    [Fact]
    public async Task 既定の上限は1GBである()
    {
        var user = $"default-{Guid.NewGuid():N}"[..24];
        var usage = await SessionAs(user)
            .GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", TestContext.Current.CancellationToken);
        usage!.Usage.LimitBytes.Should().Be(1L * 1024 * 1024 * 1024);
        usage.Usage.UsedBytes.Should().Be(0);
    }
}
