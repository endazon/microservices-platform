using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using DocumentService.Features.Documents.AddTag;
using DocumentService.Features.ObsidianSync;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes.Purge;
using DocumentService.Infrastructure.Persistence;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// 🔴 FR-02, FR-06, FR-18, FR-19, FR-20, IADR-0637 決定 7 (#2150 段 1c / #2258): **もとから並行例外を 409 へ写していなかった 8 経路は、
// 発行の連番（並行トークン）に負けても 500 にならない。**
//
// 段 1a で、Touch しない状態の変化（共有・露出・論理削除・復元・タグの改名・再発行）にも負けるようになった（窓が広がった）。
// 窓は `DocumentRace`（`AtSave`。文書行を書く保存の直前に、別のスコープで同じ文書の発行を確定させる）で決定的に開ける。
// 負けた側は確定ごと巻き戻る（発行は出ない）。勝った側の連番だけが進む。
[Trait("TestKind", "Integration")]
public class DocumentConflictMappingTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Admin = "admin-1";

    private static HttpClient AdminClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Admin);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");
        return client;
    }

    private static List<DocumentUpdated> UpdatesFor(WebApplicationFactory<Program> host, Guid id) =>
        [.. host.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == id)];

    private static long LedgerSequence(WebApplicationFactory<Program> host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.AsNoTracking().Single(d => d.Id == id).EventSequence;
    }

    private static string Status(WebApplicationFactory<Program> host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents.AsNoTracking().Single(d => d.Id == id).Status;
    }

    // 管理者が組織の文書を 1 件作る（作成で 1 回発行される。所有者は管理者）。
    private static async Task<Guid> OrgDocumentAsync(WebApplicationFactory<Program> host)
    {
        var created = await AdminClient(host).PostAsJsonAsync("/documents", new
        {
            title = $"競合-{Guid.NewGuid():N}",
            attributes = new { confidentiality = "internal", doc_scope = "organization" },
            tags = Array.Empty<string>(),
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<DocumentDto>(Ct))!.Id;
    }

    // 台帳へ本文つきの組織文書を直接置く（一回限りの管理操作の母集合に入れるため）。
    private async Task<Guid> SeedAsync(WebApplicationFactory<Program> host, string title, Dictionary<string, string> attributes,
        string? body, DateTimeOffset? createdAt = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var id = Guid.NewGuid();
        var uri = $"storage://{RecordingObjectStorageClient.Bucket}/documents/{id:D}/body.md";
        var doc = Document.CreateWithBody(id, title, uri, null, "text/markdown", attributes);
        if (body is not null) factory.Storage.Texts[uri] = body;
        db.Documents.Add(doc);
        if (createdAt is { } at) db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = at;
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task AssertLostAsync(WebApplicationFactory<Program> host, HttpResponseMessage response, Guid id,
        long before, int countBefore, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, because);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("version_conflict");
        UpdatesFor(host, id).Skip(countBefore).Select(e => e.Sequence).Should().Equal([before + 1],
            "勝った側の 1 回だけが出て、負けた側の発行は出ない");
        LedgerSequence(host, id).Should().Be(before + 1, "勝った側の連番だけが進む");
    }

    public static TheoryData<string> SingleDocumentOperations() => ["archive", "publish", "put-body"];

    [Theory]
    [MemberData(nameof(SingleDocumentOperations))]
    public async Task 単一文書の経路は並行する確定に負けたら409を返す(string operation)
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var id = await OrgDocumentAsync(racing);
        var before = LedgerSequence(racing, id);
        var countBefore = UpdatesFor(racing, id).Count;
        var client = AdminClient(racing);

        target.Id = id;
        var response = operation switch
        {
            "archive" => await client.PostAsync($"/documents/{id}/archive", null, Ct),
            "publish" => await client.PostAsync($"/documents/{id}/publish", null, Ct),
            _ => await client.PutAsJsonAsync($"/documents/{id}/body", new { body = "新しい本文" }, Ct),
        };

        target.Raced.Should().Be(1, "前提: 負ける側の保存の直前に、勝つ側が確定した");
        await AssertLostAsync(racing, response, id, before, countBefore, $"{operation}: 負けた側を 500 にしない");
    }

    [Fact]
    public async Task 同期の衝突のlocalでの解決は並行する確定に負けたら409を返す()
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var owner = $"c1c-{Guid.NewGuid():N}"[..20];
        var session = racing.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = racing.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var note = (await (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = "c1c.md", title = "c1c.md", edits = new[] { new { content = "初版" } } }, Ct))
            .Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!;
        // 勝つ側の発行が連番を進めるよう、露出を ON にする。
        (await session.PutAsJsonAsync($"/private-notes/{note.NoteId}/exposure", new UpdateExposureRequest(true, false, false), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        object Update(string content) => new
        {
            noteId = note.NoteId,
            vaultPath = "c1c.md",
            title = "c1c.md",
            baseVersion = note.Version,
            edits = new[] { new { content } },
        };
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes", Update("サーバ版"), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await plugin.PostAsJsonAsync("/private-notes/sync/notes", Update("ローカル版"), Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflicts = (await session.GetFromJsonAsync<List<SyncConflictSummaryDto>>("/private-notes/conflicts", Ct))!;
        var before = LedgerSequence(racing, note.NoteId);
        var countBefore = UpdatesFor(racing, note.NoteId).Count;

        target.Id = note.NoteId;
        var response = await session.PostAsJsonAsync($"/private-notes/conflicts/{conflicts[0].Id}/resolve",
            new ResolveSyncConflictRequest(SyncConflictResolutions.Local), Ct);

        target.Raced.Should().Be(1);
        await AssertLostAsync(racing, response, note.NoteId, before, countBefore, "local の解決を 500 にしない");
        // 衝突の記録の解決も同じ確定で巻き戻る（実 PostgreSQL のトランザクション）。InMemory はトランザクションを持たず、同じ保存の中で
        // 文書より先に書いた行が残るため、ここでは測らない。
    }

    [Fact]
    public async Task タグの追加は並行する確定に負けたら競合の結果を返し_発行は出ない()
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var tagName = $"c1c-{Guid.NewGuid():N}"[..20];
        (await AdminClient(racing).PostAsJsonAsync("/tags", new CreateTagRequest(tagName), Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await OrgDocumentAsync(racing);
        var before = LedgerSequence(racing, id);
        var countBefore = UpdatesFor(racing, id).Count;

        target.Id = id;
        AddDocumentTagOutcome outcome;
        using (var scope = racing.Services.CreateScope())
            outcome = await scope.ServiceProvider.GetRequiredService<AddDocumentTagUseCase>()
                .ExecuteAsync(id, tagName, Admin, _ => ValueTask.FromResult(true), Ct);

        target.Raced.Should().Be(1);
        outcome.Status.Should().Be(AddDocumentTagStatus.VersionConflict, "例外のまま輸送へ出さず、競合として返す");
        UpdatesFor(racing, id).Skip(countBefore).Select(e => e.Sequence).Should().Equal([before + 1]);
    }

    // gRPC は競合を `ABORTED` にする（応答を作る唯一の点 `Respond`）。既定の枝（`NOT_WRITABLE`）へ化けさせない。陽性対照も置く。
    [Fact]
    public void タグの追加の競合はgRPCでABORTEDになり書けないへ化けない()
    {
        var act = () => DocumentTagWriteGrpcService.Respond(AddDocumentTagStatus.VersionConflict);

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.Aborted);
        DocumentTagWriteGrpcService.Respond(AddDocumentTagStatus.Applied).Result
            .Should().Be(Knowledge.Contracts.Grpc.Document.V1.TagWriteResult.Applied, "陽性対照: 競合でなければ写像のとおり");
    }

    public static TheoryData<string> BatchOperations() => ["archive-ast-quotes", "archive-bodyless", "strip-untrusted-boundary"];

    [Theory]
    [MemberData(nameof(BatchOperations))]
    public async Task 複数文書の経路は並行する確定に負けたらidなしの409と再送を促す文言を返し_ページは確定しない(string operation)
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var ast = new Dictionary<string, string> { ["confidentiality"] = "internal", ["project"] = "ai-stock-trading" };
        const string open = DocumentService.Features.Documents.StripUntrustedBoundary.UntrustedBoundaryFrame.OpenMarker;
        const string close = DocumentService.Features.Documents.StripUntrustedBoundary.UntrustedBoundaryFrame.CloseMarker;
        var id = operation switch
        {
            "archive-ast-quotes" => await SeedAsync(racing, "7203 現在値", new(ast) { ["kind"] = "Quote" }, "current=1"),
            "archive-bodyless" => await SeedAsync(racing, "本文なし", new() { ["confidentiality"] = "internal" }, body: null,
                createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            _ => await SeedAsync(racing, $"{open}\n見出し\n{close}", new(ast) { ["kind"] = "News" }, $"{open}\n本文\n{close}"),
        };
        var before = LedgerSequence(racing, id);
        var countBefore = UpdatesFor(racing, id).Count;
        object request = operation == "archive-bodyless"
            ? new { dryRun = false, createdBefore = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), limit = 50, requestedBy = "alice", reason = "1c" }
            : new { dryRun = false, limit = 50, requestedBy = "alice", reason = "1c" };

        target.Id = id;
        var response = await AdminClient(racing).PostAsJsonAsync($"/documents/{operation}", request, Ct);

        target.Raced.Should().Be(1, "前提: ページの確定の直前に、同じ文書への別の確定が入った");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{operation}: 負けた側を 500 にしない");
        var body = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!;
        body["error"].Should().Be("version_conflict");
        body["message"].Should().Be(DocumentVersionConflict.BatchPageConflictMessage);
        UpdatesFor(racing, id).Skip(countBefore).Select(e => e.Sequence).Should().Equal([before + 1], "ページの発行は出ない");
        if (operation != "strip-untrusted-boundary")
            Status(racing, id).Should().NotBe(DocumentStatus.Archived, "ページは 1 件も確定していない");
    }

    // FR-19 (#2259 のレビュー 🟡 2): 完全削除の 409 は、本文の実体が消えている場合があることと再送を伝える。
    [Fact]
    public async Task 完全削除の409は本文の実体が戻らないことと再送を伝える()
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var owner = $"c1p-{Guid.NewGuid():N}"[..20];
        var session = racing.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, owner);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" }, Ct);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(Ct))!.Token;
        var plugin = racing.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var noteId = (await (await plugin.PostAsJsonAsync("/private-notes/sync/notes",
            new { vaultPath = "c1p.md", title = "c1p.md", edits = new[] { new { content = "本文" } } }, Ct))
            .Content.ReadFromJsonAsync<PushNoteResponse>(Ct))!.NoteId;
        (await session.PutAsJsonAsync($"/private-notes/{noteId}/exposure", new UpdateExposureRequest(true, false, false), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.DeleteAsync($"/private-notes/{noteId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        target.Id = noteId;
        var response = await session.PostAsJsonAsync("/private-notes/purge", new { ids = new[] { noteId } }, Ct);

        target.Raced.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!;
        body["error"].Should().Be("version_conflict");
        body["message"].Should().Be(PurgePrivateNotesEndpoint.PurgeConflictMessage);
    }
}
