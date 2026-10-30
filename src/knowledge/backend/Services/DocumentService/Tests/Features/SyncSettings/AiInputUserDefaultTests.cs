using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.SyncSettings;

// FR-19, FR-20, SC-19, SC-20 主要素 8, 計画 ADR-0143 決定 1〜4 (#2120):
// AI 入力の**利用者単位の既定**は、新しく作る個人資料（SC-19 の作成・同期の新規 push）の AI 入力の初期値である。
//
// 🔴 **陰性は陽性対照と対で置く。** 「別名コピーは OFF」「既存の資料は変わらない」は、既定を一切当てない実装でも緑になる。
// 同じ利用者の既定 ON が新規作成に効いていることを、同じ試験の中で先に確かめる。
[Trait("TestKind", "Integration")]
public class AiInputUserDefaultTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string SettingsPath = "/private-notes/sync-settings";
    private const string PushPath = "/private-notes/sync/notes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private async Task<(HttpClient Session, HttpClient Plugin)> OwnerAsync(string prefix)
    {
        var session = SessionAs(NewUser(prefix));
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (session, plugin);
    }

    private static async Task<SyncSettingsDto> PutSettingsAsync(HttpClient session, List<string> folders,
        bool? aiInputDefault)
    {
        var resp = await session.PutAsJsonAsync(SettingsPath,
            new UpdateSyncSettingsRequest(folders, aiInputDefault), Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<SyncSettingsDto>(Ct))!;
    }

    private static async Task<Guid> CreateNoteAsync(HttpClient session, string vaultPath)
    {
        var resp = await session.PostAsJsonAsync("/private-notes/",
            new { title = Path.GetFileNameWithoutExtension(vaultPath), vaultPath }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PrivateNoteDto>(Ct))!.Id;
    }

    private static async Task<PushNoteResponse> PushNewAsync(HttpClient plugin, string path, string content,
        Guid? sourceNoteId = null)
    {
        var resp = await plugin.PostAsJsonAsync(PushPath, new
        {
            noteId = (Guid?)null,
            vaultPath = path,
            title = Path.GetFileNameWithoutExtension(path),
            baseVersion = (int?)null,
            edits = new[] { new { content } },
            sourceNoteId,
        }, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!;
    }

    private static async Task<PrivateNoteDto> NoteAsync(HttpClient session, Guid id)
        => (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", Ct))!
            .Notes.Single(n => n.Id == id);

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>()
            .Where(e => e.DocumentId == documentId)];

    // 決定 1: 既定は初期状態で OFF（未設定と OFF を区別しない）。設定して読める。
    [Fact]
    public async Task 既定は未設定でOFFで設定すると読める()
    {
        var session = SessionAs(NewUser("ai-get"));

        (await session.GetFromJsonAsync<SyncSettingsDto>(SettingsPath, Ct))!.AiInputDefault
            .Should().BeFalse("未設定の利用者の既定は OFF");

        (await PutSettingsAsync(session, [], aiInputDefault: true)).AiInputDefault.Should().BeTrue();
        (await session.GetFromJsonAsync<SyncSettingsDto>(SettingsPath, Ct))!.AiInputDefault
            .Should().BeTrue("保存した既定は読み出しでも同じ");
    }

    // 省略（null）は変えない —— フォルダだけを送る既存の呼び出しが既定を OFF へ戻さない。
    [Fact]
    public async Task フォルダだけを送る更新は既定を変えない()
    {
        var session = SessionAs(NewUser("ai-keep"));
        await PutSettingsAsync(session, ["work"], aiInputDefault: true);

        var after = await PutSettingsAsync(session, ["home"], aiInputDefault: null);

        after.TargetFolders.Select(f => f.Path).Should().Equal(["home"], "陽性対照: フォルダは置き換わった");
        after.AiInputDefault.Should().BeTrue("既定は省略では変わらない");
    }

    // 決定 1・4: 既定 ON の利用者の新規作成（SC-19）は AI 入力だけ ON。横断検索・グラフは OFF のまま。
    // 本文の無い作成（0 バイト）なので発行はしない（従前どおり）。
    [Fact]
    public async Task 既定ONの利用者がSC19で作る資料はAI入力だけONになる()
    {
        var session = SessionAs(NewUser("ai-create"));
        var off = await CreateNoteAsync(session, "before.md");
        await PutSettingsAsync(session, [], aiInputDefault: true);

        var id = await CreateNoteAsync(session, "after.md");

        var note = await NoteAsync(session, id);
        note.IncludeInAi.Should().BeTrue("作成時点の既定（ON）で初期化する");
        note.IncludeInSearch.Should().BeFalse("横断検索の既定は置かない（決定 4）");
        note.IncludeInGraph.Should().BeFalse("グラフの既定は置かない（決定 4）");
        UpdatesFor(id).Should().BeEmpty("本文の無い作成は発行しない");

        // 決定 2: 既定を変えても、既に作った資料は変わらない（陽性対照は上の新規資料）。
        (await NoteAsync(session, off)).IncludeInAi.Should().BeFalse("既存の資料は既定の変更で変わらない");
    }

    // 既定 OFF（未設定）の新規作成は 3 つとも OFF（従前どおり）。
    [Fact]
    public async Task 既定OFFの利用者がSC19で作る資料は3つともOFFになる()
    {
        var session = SessionAs(NewUser("ai-create-off"));

        var note = await NoteAsync(session, await CreateNoteAsync(session, "memo.md"));

        note.IncludeInAi.Should().BeFalse();
        note.IncludeInSearch.Should().BeFalse();
        note.IncludeInGraph.Should().BeFalse();
    }

    // 決定 1・4: 既定 ON の利用者の新規 push は AI 入力だけ ON で作られ、門を通って索引へ載る（ADR-0061 決定 1）。
    // 決定 3: `sourceNoteId` 付きの新規 push（プラグインの「両方を残す」の写し）には既定を当てない。
    [Fact]
    public async Task 既定ONの利用者の新規pushはAI入力だけONで写しのpushは3つともOFFになる()
    {
        var (session, plugin) = await OwnerAsync("ai-push");
        await PutSettingsAsync(session, [], aiInputDefault: true);

        var created = await PushNewAsync(plugin, "notes/new.md", "新しい本文");
        var copy = await PushNewAsync(plugin, "notes/new (copy).md", "写しの本文", sourceNoteId: created.NoteId);

        var note = await NoteAsync(session, created.NoteId);
        note.IncludeInAi.Should().BeTrue("陽性対照: 写しでない新規 push は既定（ON）で作られる");
        note.IncludeInSearch.Should().BeFalse();
        note.IncludeInGraph.Should().BeFalse();
        UpdatesFor(created.NoteId).Should().ContainSingle("露出のどれかが ON の新規 push は門を通って 1 回発行する");

        var alias = await NoteAsync(session, copy.NoteId);
        alias.IncludeInAi.Should().BeFalse("別名コピーには既定を当てない（決定 3）");
        alias.IncludeInSearch.Should().BeFalse();
        alias.IncludeInGraph.Should().BeFalse();
        UpdatesFor(copy.NoteId).Should().BeEmpty("露出 OFF の写しは発行しない");
    }

    // 既定 OFF の利用者の新規 push は 3 つとも OFF で、何も発行しない（従前どおり）。
    [Fact]
    public async Task 既定OFFの利用者の新規pushは3つともOFFで発行しない()
    {
        var (session, plugin) = await OwnerAsync("ai-push-off");

        var created = await PushNewAsync(plugin, "notes/off.md", "本文");

        var note = await NoteAsync(session, created.NoteId);
        note.IncludeInAi.Should().BeFalse();
        note.IncludeInSearch.Should().BeFalse();
        note.IncludeInGraph.Should().BeFalse();
        UpdatesFor(created.NoteId).Should().BeEmpty();
    }

    // 決定 3: サーバ側の競合の解決「両方を残す」で作る別名コピーにも既定を当てない。
    [Fact]
    public async Task 既定ONでも競合の解決で作る別名コピーは3つともOFFになる()
    {
        var (session, plugin) = await OwnerAsync("ai-both");
        var note = await PushNewAsync(plugin, "conf/memo.md", "初版");
        (await plugin.PostAsJsonAsync(PushPath, new
        {
            noteId = note.NoteId,
            vaultPath = "conf/memo.md",
            title = "memo",
            baseVersion = note.Version,
            edits = new[] { new { content = "サーバ版" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await plugin.PostAsJsonAsync(PushPath, new
        {
            noteId = note.NoteId,
            vaultPath = "conf/memo.md",
            title = "memo",
            baseVersion = note.Version,
            edits = new[] { new { content = "ローカル版" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await PutSettingsAsync(session, [], aiInputDefault: true);
        var conflicts = (await session.GetFromJsonAsync<List<SyncConflictSummaryDto>>(
            "/private-notes/conflicts", Ct))!;
        conflicts.Should().ContainSingle();

        var resolved = await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Both), Ct);
        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdId = (await resolved.Content.ReadFromJsonAsync<ResolveSyncConflictResponse>(Ct))!
            .CreatedNoteId!.Value;

        // 陽性対照: 同じ利用者の既定は ON で、新規作成には効いている。
        (await NoteAsync(session, await CreateNoteAsync(session, "conf/fresh.md"))).IncludeInAi.Should().BeTrue();
        var alias = await NoteAsync(session, createdId);
        alias.IncludeInAi.Should().BeFalse("別名コピーには既定を当てない（決定 3）");
        alias.IncludeInSearch.Should().BeFalse();
        alias.IncludeInGraph.Should().BeFalse();
    }

    // 決定 2: 既定を OFF へ戻しても、既定 ON の間に作った資料は ON のまま。
    [Fact]
    public async Task 既定をOFFへ戻しても既に作った資料は変わらない()
    {
        var session = SessionAs(NewUser("ai-revert"));
        await PutSettingsAsync(session, [], aiInputDefault: true);
        var id = await CreateNoteAsync(session, "on.md");
        (await NoteAsync(session, id)).IncludeInAi.Should().BeTrue("前提: 既定 ON の間に作った資料は ON");

        await PutSettingsAsync(session, [], aiInputDefault: false);

        (await NoteAsync(session, id)).IncludeInAi.Should().BeTrue("既定の変更は既存の資料に触れない");
        (await NoteAsync(session, await CreateNoteAsync(session, "off.md"))).IncludeInAi
            .Should().BeFalse("陽性対照: 戻した既定は新しい資料に効いている");
    }

    // マイグレーション（AddSyncSettingsAiInputDefault）がモデルと一致する。
    [Fact]
    public void スナップショットとモデルに差が無い()
    {
        using var db = new DocumentDbContext(new DbContextOptionsBuilder<DocumentDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused").Options);

        db.Database.HasPendingModelChanges().Should().BeFalse();
        db.Database.GetMigrations().Should().Contain(m => m.EndsWith("_AddSyncSettingsAiInputDefault",
            StringComparison.Ordinal));
    }
}
