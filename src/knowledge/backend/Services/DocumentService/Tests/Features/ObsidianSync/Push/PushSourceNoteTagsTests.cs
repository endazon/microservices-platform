using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.ObsidianSync.Push;

// FR-20, UC-11, SC-20 主要素 5, ADR-0105 決定 1〜4, ADR-0110（planning#652 の裁定 1）, [[IADR-0464]] (#1521):
// プラグインの「両方を残す」は写しを新規 push で送る。push の `sourceNoteId` が**同じ所有者の個人資料**を指すときだけ、
// その資料のタグを写す（露出・共有先・版履歴・機密区分は引き継がない）。
//
// 🔴 **陰性は陽性対照と対で置く。** 「他者の資料を指すと写らない」は「何も写さない実装」でも緑になる。
// 同じ要求の形で自分の資料なら写ることを同じ試験の中で先に確かめる。
[Trait("TestKind", "Integration")]
public class PushSourceNoteTagsTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string PushPath = "/private-notes/sync/notes";

    private HttpClient SessionAs(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private HttpClient TagAdmin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");
        return client;
    }

    private static string NewUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<(string User, HttpClient Session, HttpClient Plugin)> OwnerAsync(string prefix)
    {
        var user = NewUser(prefix);
        var session = SessionAs(user);
        var issued = await session.PostAsJsonAsync("/private-notes/devices", new { deviceName = "pc" },
            TestContext.Current.CancellationToken);
        issued.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await issued.Content.ReadFromJsonAsync<SyncTokenIssuedResponse>(
            TestContext.Current.CancellationToken))!.Token;
        var plugin = factory.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, session, plugin);
    }

    // プラグインの `conflictResolver.ts` の `both` と同じ形（noteId・baseVersion は null、写しの本文だけ）。
    private static Task<HttpResponseMessage> PushNewAsync(HttpClient plugin, string path,
        Guid? sourceNoteId, params string[] edits)
        => plugin.PostAsJsonAsync(PushPath, new
        {
            noteId = (Guid?)null,
            vaultPath = path,
            title = Path.GetFileNameWithoutExtension(path),
            baseVersion = (int?)null,
            edits = edits.Select(c => new { content = c }).ToList(),
            sourceNoteId,
        }, TestContext.Current.CancellationToken);

    private static async Task<PushNoteResponse> CreatedAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PushNoteResponse>(
            TestContext.Current.CancellationToken))!;
    }

    // 資料にタグ 2 つ・共有 1 件・既定と違う機密区分を与える（露出は呼び出し側が API で ON にする）。
    private async Task<(Tag A, Tag B)> DecorateAsync(Guid documentId, string grantedBy)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Tag.Create($"写元{Guid.NewGuid():N}"[..12]);
        var b = Tag.Create($"写元{Guid.NewGuid():N}"[..12]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.Tags.AddRange(a, b);
        var doc = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        var attributes = new Dictionary<string, string>(doc.Attributes)
        {
            [DocumentAttributes.ConfidentialityKey] = "internal",
        };
        doc.UpdateMetadata(attributes, [a.Id, b.Id], "test-decorate");
        db.DocumentShares.Add(DocumentShare.Create(documentId, "user", "colleague-1", grantedBy));
        await db.SaveChangesAsync(ct);
        return (a, b);
    }

    private async Task<List<Guid>> TagsOfAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        return [.. (await db.Documents.FirstAsync(d => d.Id == documentId,
            TestContext.Current.CancellationToken)).Tags];
    }

    private async Task<int> UsageCountAsync(Guid tagId)
    {
        var body = await TagAdmin().GetFromJsonAsync<TagDictionaryResponse>("/tags",
            TestContext.Current.CancellationToken);
        return body!.Tags.Single(t => t.Id == tagId).UsageCount;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static List<string> KeysOf(string json)
    {
        using var body = JsonDocument.Parse(json);
        return [.. body.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private List<DocumentUpdated> UpdatesFor(Guid documentId) =>
        [.. factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>()
            .Where(e => e.DocumentId == documentId)];

    [Fact]
    public async Task sourceNoteIdが自分の資料を指すとタグだけを写し露出も共有先も版も機密区分も引き継がない()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, session, plugin) = await OwnerAsync("src-own");
        var source = await CreatedAsync(await PushNewAsync(plugin, "notes/元.md", null, "v1"));
        (await session.PutAsJsonAsync($"/private-notes/{source.NoteId}/exposure",
            new UpdateExposureRequest(true, false, false), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var (a, b) = await DecorateAsync(source.NoteId, user);

        // 陽性対照: 元の資料は露出 ON・共有 1 件・機密区分が既定（restricted）と違う・タグ 2 つ。
        var before = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!
            .Notes.Single(n => n.Id == source.NoteId);
        before.IncludeInSearch.Should().BeTrue("前提: 元の資料は露出 ON");
        before.SharedUserCount.Should().Be(1, "前提: 元の資料は 1 人に共有されている");
        before.Tags.Should().BeEquivalentTo([a.Name, b.Name]);
        (await UsageCountAsync(a.Id)).Should().Be(1, "前提: 元の資料だけがタグを参照している");

        var copy = await CreatedAsync(await PushNewAsync(plugin, "notes/元 (ローカル 20260926-0900).md",
            source.NoteId, "local e1", "local e2"));

        // ADR-0105 決定 3: タグは引き継ぐ（識別子の集合をそのまま・並びも保つ）。
        (await TagsOfAsync(copy.NoteId)).Should().Equal(await TagsOfAsync(source.NoteId));
        (await UsageCountAsync(a.Id)).Should().Be(2, "決定 3 が受け入れた副作用: 写しの分だけ使用件数が増える");
        (await UsageCountAsync(b.Id)).Should().Be(2);

        var created = (await session.GetFromJsonAsync<PrivateNoteListResponse>("/private-notes/", ct))!
            .Notes.Single(n => n.Id == copy.NoteId);
        // 決定 1・4: 露出 3 トグルは引き継がない。
        created.IncludeInSearch.Should().BeFalse();
        created.IncludeInGraph.Should().BeFalse();
        created.IncludeInAi.Should().BeFalse();
        // 決定 2: 共有先は引き継がない。
        created.Visibility.Should().Be(PrivateNoteVisibilityValues.Private);
        created.SharedUserCount.Should().Be(0);
        // 決定 3: 版履歴は引き継がない（写しの edits の数から始まる）。
        var pulled = await plugin.GetFromJsonAsync<PullNoteResponse>($"{PushPath}/{copy.NoteId}", ct);
        pulled!.Version.Should().Be(2, "写しの版は送った edits の 2 つだけ");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            (await db.DocumentShares.CountAsync(s => s.DocumentId == copy.NoteId, ct))
                .Should().Be(0, "共有台帳へ利用者の明示操作なしに行を足さない");
            var doc = await db.Documents.FirstAsync(d => d.Id == copy.NoteId, ct);
            doc.Attributes.Should().ContainKey(DocumentAttributes.ConfidentialityKey)
                .WhoseValue.Should().Be("restricted", "機密区分は個人資料の既定へ戻る（元の資料は internal）");
            foreach (var key in DocumentExposure.AllKeys)
                doc.Attributes.Should().ContainKey(key)
                    .WhoseValue.Should().Be(DocumentExposure.Excluded, $"{key} は明示の OFF が書かれている");
        }

        UpdatesFor(copy.NoteId).Should().BeEmpty("露出 OFF の写しは索引の生産側へ流れない");
    }

    [Fact]
    public async Task sourceNoteIdが他者の資料を指すと何も写さず応答も変わらない()
    {
        var ct = TestContext.Current.CancellationToken;
        var (bob, _, bobPlugin) = await OwnerAsync("src-bob");
        var bobNote = await CreatedAsync(await PushNewAsync(bobPlugin, "bob/秘密.md", null, "bob only"));
        var (bobA, _) = await DecorateAsync(bobNote.NoteId, bob);
        var bobTagsBefore = await TagsOfAsync(bobNote.NoteId);

        var (alice, _, alicePlugin) = await OwnerAsync("src-alice");
        var aliceNote = await CreatedAsync(await PushNewAsync(alicePlugin, "alice/元.md", null, "a"));
        await DecorateAsync(aliceNote.NoteId, alice);

        // 陽性対照: 同じ要求の形で、自分の資料を指せば写る。
        var ownCopy = await CreatedAsync(await PushNewAsync(alicePlugin, "alice/自分の写し.md",
            aliceNote.NoteId, "copy"));
        (await TagsOfAsync(ownCopy.NoteId)).Should().HaveCount(2, "前提: 同じ形の要求で自分の資料なら写る");

        // 否定形: 他者の資料を指しても、201 で作られタグは空。他者のタグの使用件数も動かない。
        var foreign = await PushNewAsync(alicePlugin, "alice/他者の写し.md", bobNote.NoteId, "copy");
        foreign.StatusCode.Should().Be(HttpStatusCode.Created, "他者の資料を指しても拒否しない");
        var foreignBody = await foreign.Content.ReadAsStringAsync(ct);
        var foreignCopy = JsonSerializer.Deserialize<PushNoteResponse>(foreignBody, Web)!;
        (await TagsOfAsync(foreignCopy.NoteId)).Should().BeEmpty("他者の資料のタグを写さない");
        (await UsageCountAsync(bobA.Id)).Should().Be(1, "他者のタグの参照は増えない");
        (await TagsOfAsync(bobNote.NoteId)).Should().Equal(bobTagsBefore, "他者の資料は変わらない");

        // 応答の形は `sourceNoteId` 無しのときと同じ（状態とキーの集合が同じ＝他者の資料の有無を応答で探れない）。
        var plain = await PushNewAsync(alicePlugin, "alice/無指定.md", null, "copy");
        plain.StatusCode.Should().Be(foreign.StatusCode);
        KeysOf(foreignBody).Should().BeEquivalentTo(KeysOf(await plain.Content.ReadAsStringAsync(ct)));
    }

    public static TheoryData<string> Unreachable() => ["missing", "organization-document"];

    [Theory]
    [MemberData(nameof(Unreachable))]
    public async Task sourceNoteIdが存在しない資料や組織文書を指すと何も写さない(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        Guid target;
        if (kind == "missing")
        {
            target = Guid.NewGuid();
        }
        else
        {
            // 組織文書（管理者経路・doc_scope 無し）。個人資料の台帳に行が無いので同期資格情報から届かない。
            var org = await SessionAs("admin").PostAsJsonAsync("/documents", new
            {
                title = "組織文書",
                attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
                tags = new List<string>(),
            }, ct);
            org.StatusCode.Should().Be(HttpStatusCode.Created);
            target = (await org.Content.ReadFromJsonAsync<DocumentDto>(ct))!.Id;
            await DecorateAsync(target, "admin");
            (await TagsOfAsync(target)).Should().HaveCount(2, "前提: 組織文書はタグを持つ");
        }

        var (_, _, plugin) = await OwnerAsync($"src-{kind[..4]}");
        var copy = await CreatedAsync(await PushNewAsync(plugin, "notes/写し.md", target, "copy"));

        (await TagsOfAsync(copy.NoteId)).Should().BeEmpty();
    }

    [Fact]
    public async Task sourceNoteIdが無ければ従来どおりタグは空()
    {
        var (_, _, plugin) = await OwnerAsync("src-none");

        var created = await CreatedAsync(await PushNewAsync(plugin, "notes/新規.md", null, "new"));

        (await TagsOfAsync(created.NoteId)).Should().BeEmpty();
    }

    [Fact]
    public async Task 更新のpushではsourceNoteIdを読まない()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, _, plugin) = await OwnerAsync("src-upd");
        var tagged = await CreatedAsync(await PushNewAsync(plugin, "notes/タグあり.md", null, "t"));
        await DecorateAsync(tagged.NoteId, user);
        var target = await CreatedAsync(await PushNewAsync(plugin, "notes/更新先.md", null, "v1"));

        var update = await plugin.PostAsJsonAsync(PushPath, new
        {
            noteId = target.NoteId,
            vaultPath = "notes/更新先.md",
            title = "更新先",
            baseVersion = target.Version,
            edits = new[] { new { content = "v2" } },
            sourceNoteId = tagged.NoteId,
        }, ct);

        update.StatusCode.Should().Be(HttpStatusCode.OK, "更新そのものは通る");
        (await TagsOfAsync(target.NoteId)).Should().BeEmpty("既存の資料のタグを push で書き換える経路を作らない");
    }
}
