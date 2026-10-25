using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using DocumentService.Features.Documents.Republish;
using DocumentService.Features.Tags.Rename;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// 🔴 FR-02, FR-09, IADR-0637 決定 6 (#2150 段 1b): **多数の文書を 1 回で確定する口（タグの改名・再発行）の読み直しとやり直し。**
//
// - 発行の連番（並行トークン）に負けたら、全体（再発行はページ）を読み直してやり直す。上限 `MaxBatchCommitAttempts` の内なら確定する。
// - 上限を超えたら何も確定せず 409 `version_conflict` を返す。改名は SC-09 が出す `message` を載せ、再発行は負けたページの先頭のカーソルを載せる。
// - 再開は冪等である（改名済みの名前への再送は辞書を変えずに再発行だけを行う。再発行の重複は受け手の版の印で吸収される）。
//
// 🔴 InMemory はトランザクションを持たないので、「何も確定しない」（辞書の名前が巻き戻る）は実 PostgreSQL の
// `Knowledge.IntegrationTests` の `DocumentBatchCommitRetryTests` が測る。ここでは応答と、負けた側の発行が出ないことを測る。
[Trait("TestKind", "Integration")]
public class DocumentBatchCommitRetryTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int MaxAttempts = DocumentVersionConflict.MaxBatchCommitAttempts;

    private static HttpClient Admin(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "admin-1");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");
        return client;
    }

    private static List<DocumentUpdated> UpdatesFor(WebApplicationFactory<Program> host, Guid id) =>
        [.. host.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == id)];

    // タグを 1 つ作り、それを持つ組織の文書を 1 件作る（作成で 1 回発行される）。
    private static async Task<(Guid TagId, Guid DocId, long Sequence)> TaggedDocumentAsync(WebApplicationFactory<Program> host)
    {
        var client = Admin(host);
        var tagName = $"seq-{Guid.NewGuid():N}"[..20];
        var tag = await client.PostAsJsonAsync("/tags", new CreateTagRequest(tagName), Ct);
        tag.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagId = (await tag.Content.ReadFromJsonAsync<TagDto>(Ct))!.Id;
        var doc = await client.PostAsJsonAsync("/documents", new
        {
            title = $"連番-{Guid.NewGuid():N}",
            attributes = new { confidentiality = "internal", doc_scope = "organization" },
            tags = new[] { tagName },
        }, Ct);
        doc.StatusCode.Should().Be(HttpStatusCode.Created);
        var docId = (await doc.Content.ReadFromJsonAsync<DocumentDto>(Ct))!.Id;
        return (tagId, docId, UpdatesFor(host, docId)[^1].Sequence!.Value);
    }

    [Fact]
    public async Task 改名は1回負けても読み直して確定し_勝った側の後の連番で再発行する()
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var (tagId, docId, before) = await TaggedDocumentAsync(racing);
        var renamed = $"seq-renamed-{Guid.NewGuid():N}"[..28];

        target.Id = docId;
        var response = await Admin(racing).PutAsJsonAsync($"/tags/{tagId}", new RenameTagRequest(renamed), Ct);

        target.Raced.Should().Be(1, "前提: 1 回目の確定の直前に、勝つ側が確定した");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "上限の内の負けは読み直してやり直す");
        (await response.Content.ReadFromJsonAsync<RenameTagResponse>(Ct))!.RepublishedDocuments.Should().Be(1);
        var after = UpdatesFor(racing, docId).Where(e => e.Sequence > before).ToList();
        after.Select(e => e.Sequence).Should().Equal([before + 1, before + 2], "勝った側、その後にやり直した改名の再発行");
        after[^1].Tags.Should().Contain(renamed, "やり直した再発行は新しい名前を運ぶ");
    }

    [Fact]
    public async Task 改名は上限まで負けたら409とSC09が出す文言を返し_負けた側の発行は出ない()
    {
        var target = new RaceTarget { Remaining = MaxAttempts, AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var (tagId, docId, before) = await TaggedDocumentAsync(racing);

        target.Id = docId;
        var response = await Admin(racing).PutAsJsonAsync($"/tags/{tagId}",
            new RenameTagRequest($"seq-renamed-{Guid.NewGuid():N}"[..28]), Ct);

        target.Raced.Should().Be(MaxAttempts, "前提: 毎回の確定の直前に、勝つ側が確定した");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "上限を超えた負けを 500 にしない");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct);
        body!["error"].Should().Be("version_conflict");
        // SC-09 は 409 の本文の `message` を表示する（`parseProblemDetails` → `toMessages`。BFF は本文を透過する）。
        body["message"].Should().Be(RenameTagEndpoint.RenameConflictMessage);
        UpdatesFor(racing, docId).Where(e => e.Sequence > before).Select(e => e.Sequence)
            .Should().Equal([before + 1, before + 2, before + 3], "出たのは勝った側の 3 回だけで、改名の再発行は 1 通も出ない");
    }

    [Fact]
    public async Task 改名済みの名前への再送は辞書を変えずに再発行だけを行う()
    {
        var (tagId, docId, before) = await TaggedDocumentAsync(factory);
        var renamed = $"seq-renamed-{Guid.NewGuid():N}"[..28];
        var client = Admin(factory);

        (await client.PutAsJsonAsync($"/tags/{tagId}", new RenameTagRequest(renamed), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var resent = await client.PutAsJsonAsync($"/tags/{tagId}", new RenameTagRequest(renamed), Ct);

        resent.StatusCode.Should().Be(HttpStatusCode.OK, "再送（409 の後の再開）は同名の重複として拒まない");
        var result = (await resent.Content.ReadFromJsonAsync<RenameTagResponse>(Ct))!;
        result.Tag.Name.Should().Be(renamed, "辞書は変わらない");
        result.RepublishedDocuments.Should().Be(1);
        var after = UpdatesFor(factory, docId).Where(e => e.Sequence > before).ToList();
        after.Select(e => e.Sequence).Should().Equal([before + 1, before + 2], "再送は再発行だけを行い、連番は進む（重複は受け手の印が吸収する）");
        after.Should().AllSatisfy(e => e.Tags.Should().Contain(renamed));
    }

    [Fact]
    public async Task 再発行は1回負けても読み直して確定する()
    {
        var target = new RaceTarget { AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var (_, docId, before) = await TaggedDocumentAsync(racing);

        target.Id = docId;
        var response = await Admin(racing).PostAsJsonAsync("/documents/republish-updated",
            new RepublishDocumentUpdatedRequest(DryRun: false, Ids: [docId]), Ct);

        target.Raced.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RepublishDocumentUpdatedResponse>(Ct))!.Published.Should().Be(1);
        UpdatesFor(racing, docId).Where(e => e.Sequence > before).Select(e => e.Sequence).Should().Equal([before + 1, before + 2]);
    }

    [Fact]
    public async Task 再発行は上限まで負けたら負けたページの先頭のカーソルを返し_そこからの再開で発行する()
    {
        var target = new RaceTarget { Remaining = MaxAttempts, AtSave = true };
        using var racing = DocumentRace.Host(factory, target);
        var (_, first, _) = await TaggedDocumentAsync(racing);
        var (_, second, before) = await TaggedDocumentAsync(racing);
        var client = Admin(racing);

        // 1 ページ目（first）は競合なしで確定し、2 ページ目の先頭のカーソルを受け取る。
        var page1 = await client.PostAsJsonAsync("/documents/republish-updated",
            new RepublishDocumentUpdatedRequest(DryRun: false, Limit: 1, Ids: [first, second]), Ct);
        var cursor = (await page1.Content.ReadFromJsonAsync<RepublishDocumentUpdatedResponse>(Ct))!.NextCursor;
        cursor.Should().NotBeNull("前提: 2 ページ目がある");

        target.Id = second;
        var lost = await client.PostAsJsonAsync("/documents/republish-updated",
            new RepublishDocumentUpdatedRequest(DryRun: false, Limit: 1, Cursor: cursor, Ids: [first, second]), Ct);

        target.Raced.Should().Be(MaxAttempts);
        lost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = (await lost.Content.ReadFromJsonAsync<RepublishConflictResponse>(Ct))!;
        conflict.Error.Should().Be("version_conflict");
        conflict.ResumeCursor.Should().Be(cursor, "再開は負けたページの先頭から（部分の確定は無い）");
        UpdatesFor(racing, second).Where(e => e.Sequence > before).Select(e => e.Sequence)
            .Should().Equal([before + 1, before + 2, before + 3], "負けたページの発行は出ない");

        var resumed = await client.PostAsJsonAsync("/documents/republish-updated",
            new RepublishDocumentUpdatedRequest(DryRun: false, Limit: 1, Cursor: conflict.ResumeCursor, Ids: [first, second]), Ct);
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = (await resumed.Content.ReadFromJsonAsync<RepublishDocumentUpdatedResponse>(Ct))!;
        page2.Published.Should().Be(1);
        page2.NextCursor.Should().BeNull("2 ページ目で終わる");
        UpdatesFor(racing, second)[^1].Sequence.Should().Be(before + 4);
    }
}
