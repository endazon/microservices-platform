using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// 🔴 FR-02, FR-19, IADR-0637 (#2150 / #2251) 段 1a: **文書ごとの単調な発行の連番。**
//
// - 発行のたびに 1 つ進む（`UpdatedAt` を進めない変化 —— 露出・共有・論理削除・復元 —— を含む）。`DocumentDeleted` は最後の値 + 1 を運ぶ。
// - 連番は並行トークンである。並行する 2 つの状態の変化は、後から確定する側が負けて 409 `version_conflict` になり、勝った側の連番だけが進む。
// 受け手（取り込み・グラフ・Wiki）はまだ連番を読まない（段 2）。受け手の挙動が変わらないことは契約の試験と受け手の既存の試験が示す。
[Trait("TestKind", "Integration")]
public class DocumentEventSequenceTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    private List<DocumentUpdated> UpdatesFor(Guid id) => UpdatesFor(factory, id);

    private static List<DocumentUpdated> UpdatesFor(WebApplicationFactory<Program> host, Guid id) =>
        [.. host.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == id)];

    private static HttpClient SessionAs(WebApplicationFactory<Program> host, string user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private Task<(HttpClient Plugin, HttpClient Session, Guid NoteId)> ExposedNoteAsync() => ExposedNoteAsync(factory);

    // 露出 ON の個人資料（全ての状態の変化で発行される）を 1 件作る。
    private static async Task<(HttpClient Plugin, HttpClient Session, Guid NoteId)> ExposedNoteAsync(WebApplicationFactory<Program> host)
    {
        var owner = $"seq-{Guid.NewGuid():N}"[..20];
        var session = SessionAs(host, owner);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = host.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var push = await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = "seq.md", title = "seq.md", edits = new[] { new { content = "本文" } } }, Ct);
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var noteId = (await push.Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!.NoteId;
        (await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure", new UpdateExposureRequest(true, false, false), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (plugin, session, noteId);
    }

    private long LedgerSequence(Guid id) => LedgerSequence(factory, id);

    private static long LedgerSequence(WebApplicationFactory<Program> host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.Single(d => d.Id == id).EventSequence;
    }

    private static int DocumentVersion(WebApplicationFactory<Program> host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.Single(d => d.Id == id).Version;
    }

    [Fact]
    public async Task 発行のたびに連番が1つ進み_版を進めない状態の変化も含む()
    {
        var (_, session, noteId) = await ExposedNoteAsync();
        var first = UpdatesFor(noteId)[^1].Sequence!.Value;

        (await session.PostAsJsonAsync($"/documents/{noteId}/shares", new { subjectType = "user", subjectId = "bob" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await session.DeleteAsync($"/documents/{noteId}/shares/user/bob", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.PostAsync($"/private-notes/{noteId}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatesFor(noteId).Select(e => e.Sequence).Should().EndWith([first + 1, first + 2, first + 3, first + 4],
            "共有の付与・取り消し・論理削除・復元は UpdatedAt を進めないが、連番は 1 つずつ進む");
        UpdatesFor(noteId).Skip(UpdatesFor(noteId).Count - 5).Select(e => e.UpdatedAt).Distinct().Should().ContainSingle(
            "前提: この 5 件は UpdatedAt では順序を区別できない（#2150 / #2251 の根）");
        LedgerSequence(noteId).Should().Be(first + 4, "台帳の連番は最後に発行した値");
    }

    [Fact]
    public async Task 削除は最後の連番に1を足した値を運ぶ()
    {
        var (_, session, noteId) = await ExposedNoteAsync();
        await session.DeleteAsync($"/private-notes/{noteId}", Ct);
        var last = UpdatesFor(noteId)[^1].Sequence!.Value;

        (await session.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { noteId } }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        Bus.PublishedOf<DocumentDeleted>().Should().ContainSingle(e => e.DocumentId == noteId)
            .Which.Sequence.Should().Be(last + 1);
    }

    // ── 並行（決定的に窓を開ける。`DocumentRace`） ──

    // soft-delete は SC-19 の削除、plugin-delete は Obsidian 側の削除（同じ確定の関数を通る別の入口）。
    // push は Obsidian 側の既存ノートの更新（Touch する。照合済みの baseVersion の後に Touch しない変化が割り込む）、
    // purge は完全削除（複数件の行の削除。id なしの 409）。
    public static TheoryData<string> Operations() => ["grant", "revoke", "soft-delete", "plugin-delete", "restore", "push", "purge"];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task 並行する状態の変化に負けた側は409になり勝った側の連番だけが進む(string operation)
    {
        // 準備から操作まで、同じ（競合を差し込む）ホストで行う（台帳と記録のバスを 1 つにする）。
        var target = new RaceTarget();
        using var racing = DocumentRace.Host(factory, target);
        var (plugin, session, noteId) = await ExposedNoteAsync(racing);
        if (operation == "revoke")
            await session.PostAsJsonAsync($"/documents/{noteId}/shares", new { subjectType = "user", subjectId = "bob" }, Ct);
        if (operation is "restore" or "purge")
            await session.DeleteAsync($"/private-notes/{noteId}", Ct);
        var before = LedgerSequence(racing, noteId);
        var countBefore = UpdatesFor(racing, noteId).Count;
        target.Id = noteId;

        var response = operation switch
        {
            "grant" => await session.PostAsJsonAsync($"/documents/{noteId}/shares", new { subjectType = "user", subjectId = "carol" }, Ct),
            "revoke" => await session.DeleteAsync($"/documents/{noteId}/shares/user/bob", Ct),
            "soft-delete" => await session.DeleteAsync($"/private-notes/{noteId}", Ct),
            "plugin-delete" => await plugin.PostAsync($"/private-notes/sync/notes/{noteId}/delete", null, Ct),
            "push" => await plugin.PostAsJsonAsync("/private-notes/sync/notes", new
            {
                noteId,
                vaultPath = "seq.md",
                title = "seq.md",
                baseVersion = DocumentVersion(racing, noteId),
                edits = new[] { new { content = "本文の更新" } },
            }, Ct),
            "purge" => await session.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { noteId } }, Ct),
            _ => await session.PostAsync($"/private-notes/{noteId}/restore", null, Ct),
        };

        target.Raced.Should().Be(1, "前提: 窓が開いた（勝つ側が先に確定した）");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{operation}: 負けた側を 500 にしない");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("version_conflict");
        UpdatesFor(racing, noteId).Skip(countBefore).Select(e => e.Sequence).Should().Equal([before + 1],
            "勝った側の 1 回だけが出て、負けた側の発行は出ない");
        LedgerSequence(racing, noteId).Should().Be(before + 1, "勝った側の連番だけが進む（purge でも行は残る）");
        UpdatesFor(racing, noteId).Should().NotContain(e => e.Sequence > before + 1);
        racing.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentDeleted>()
            .Should().NotContain(e => e.DocumentId == noteId, "負けた完全削除の発行は巻き戻る");
    }
}
