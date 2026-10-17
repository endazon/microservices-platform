using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.ArchiveAstQuotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.ArchiveAstQuotes;

// FR-06, UC-03, SC-05, [[IADR-0549]]（2026-10-17 追記 / #1974）:
// **AST の相場スナップショット（kind = Quote）の一括アーカイブの口（`POST /documents/archive-ast-quotes`）。**
//
// 🔴 **対象外を絶対に触らない**ことを固定する: 他の kind（News・Disclosure・報告書の Daily など）・kind の大文字小文字違い・他の project・
//   project なし・個人資料・アーカイブ済み。触らないとは「状態・版・更新時刻が動かず、イベントも出ない」ことである。
// 🔴 **変異試験の対象である**（作業仕様書 §変異試験）: `AdminOnly` を外す・dry-run でもアーカイブする・アーカイブ済みを母集合から外さない・
//   kind の絞り込みを外す・project の絞り込みを外す、のいずれでも落ちる。
[Trait("TestKind", "Integration")]
public class ArchiveAstQuotesEndpointTests
{
    private const string Path = "/documents/archive-ast-quotes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient ClientAs(TestWebApplicationFactory factory, string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static HttpClient Admin(TestWebApplicationFactory factory) => ClientAs(factory, "admin-1", "platform-admin");

    private static async Task<Document> SeedAsync(TestWebApplicationFactory factory, string? kind = "Quote",
        string? project = "ai-stock-trading", bool privateNote = false, bool archived = false, DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var id = Guid.NewGuid();
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal", ["symbol"] = "7203" };
        if (kind is not null) attributes["kind"] = kind;
        if (project is not null) attributes["project"] = project;
        if (privateNote)
        {
            attributes[DocumentScopes.Key] = DocumentScopes.PrivateNote;
            attributes["owner"] = "alice";
        }

        var uri = $"storage://{RecordingObjectStorageClient.Bucket}/documents/{id:D}/body.md";
        var doc = Document.CreateWithBody(id, "7203 現在値", uri, null, "text/markdown", attributes);
        factory.Storage.Texts[uri] = "current=1;high=2;low=0;prevClose=1";
        if (archived) doc.Archive();
        db.Documents.Add(doc);
        if (createdAt is { } at) db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = at;
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private sealed record Snapshot(string Status, int Version, DateTimeOffset UpdatedAt, int Versions, string? LastNote);

    private static async Task<Snapshot> ReadAsync(TestWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var d = await db.Documents.AsNoTracking().Include(x => x.Versions).SingleAsync(x => x.Id == id, Ct);
        return new Snapshot(d.Status, d.Version, d.UpdatedAt, d.Versions.Count, d.Versions.OrderBy(v => v.Version).Last().ChangeNote);
    }

    private static IReadOnlyList<DocumentUpdated> Published(TestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>();

    private static object Body_(bool dryRun, int? limit = null, string? cursor = null, string? reason = "AST の Quote をアーカイブ")
        => new { dryRun, limit, cursor, requestedBy = "alice", reason };

    private static async Task<ArchiveAstQuotesResponse> CallAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync(Path, body, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<ArchiveAstQuotesResponse>(Ct))!;
    }

    private static async Task<List<ArchiveAstQuotesResponse>> RunAllAsync(HttpClient client, bool dryRun, int limit)
    {
        var pages = new List<ArchiveAstQuotesResponse>();
        string? cursor = null;
        do
        {
            var r = await CallAsync(client, Body_(dryRun, limit, cursor));
            pages.Add(r);
            cursor = r.NextCursor;
        } while (cursor is not null && pages.Count < 50);
        return pages;
    }

    [Fact]
    public async Task 管理者だけが呼べ_運用者の人とAST_の書き手と一般の利用者は403で何もアーカイブしない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory);
        var body = Body_(dryRun: false);

        (await ClientAs(factory, "operator-1", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "service-account-ai-stock-trading-kb-writer", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "user-1", "platform-user").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync(factory, doc.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        Published(factory).Should().BeEmpty();

        // 陽性対照: 同じ要求を管理者が送ればアーカイブされる。
        (await CallAsync(Admin(factory), body)).Archived.Should().Be(1);
        (await ReadAsync(factory, doc.Id)).Status.Should().Be(DocumentStatus.Archived);
    }

    [Fact]
    public void 口は_AdminOnly_を積む()
    {
        using var factory = new TestWebApplicationFactory();
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.EndsWith(ArchiveAstQuotesEndpoint.Route, StringComparison.Ordinal) == true);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
            .Should().Contain(PlatformAuthPolicies.AdminOnly);
    }

    [Theory]
    [InlineData("""{"limit":10}""", "dryRun")]
    [InlineData("""{"dryRun":false}""", "reason")]
    [InlineData("""{"dryRun":false,"reason":"  "}""", "reason")]
    [InlineData("""{"dryRun":true,"limit":0}""", "limit")]
    [InlineData("""{"dryRun":true,"limit":201}""", "limit")]
    [InlineData("""{"dryRun":true,"cursor":"not-a-cursor"}""", "cursor")]
    public async Task 必須の指定が無い_範囲外_不正なカーソルは400でその鍵を返し何もアーカイブしない(string json, string key)
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory);

        var resp = await Admin(factory).PostAsync(Path,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"{key}\"");
        (await ReadAsync(factory, doc.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        Published(factory).Should().BeEmpty();
    }

    [Fact]
    public async Task dry_run_は件数と対象のIDだけを返し_本番は_AST_の_Quote_だけをアーカイブし_対象外には触らない()
    {
        await using var factory = new TestWebApplicationFactory();
        var quotes = new[] { await SeedAsync(factory), await SeedAsync(factory), await SeedAsync(factory) };
        var untouched = new[]
        {
            await SeedAsync(factory, kind: "News"),
            await SeedAsync(factory, kind: "Disclosure"),
            await SeedAsync(factory, kind: "Daily"),
            await SeedAsync(factory, kind: "quote"),
            await SeedAsync(factory, kind: null),
            await SeedAsync(factory, project: "other"),
            await SeedAsync(factory, project: null),
            await SeedAsync(factory, privateNote: true),
        };
        var alreadyArchived = await SeedAsync(factory, archived: true);
        var before = new Dictionary<Guid, Snapshot>();
        foreach (var d in quotes.Concat(untouched).Append(alreadyArchived)) before[d.Id] = await ReadAsync(factory, d.Id);
        var admin = Admin(factory);
        var reads = factory.Storage.GetTextCallCount;

        var dry = await RunAllAsync(admin, dryRun: true, limit: 2);
        dry.Sum(p => p.Selected).Should().Be(3);
        dry.Sum(p => p.Archived).Should().Be(0);
        dry.SelectMany(p => p.Ids).Should().BeEquivalentTo(quotes.Select(q => q.Id));
        foreach (var (id, snap) in before) (await ReadAsync(factory, id)).Should().BeEquivalentTo(snap, "dry-run は何も動かさない");
        Published(factory).Should().BeEmpty();

        var applied = await RunAllAsync(admin, dryRun: false, limit: 2);
        applied.Sum(p => p.Archived).Should().Be(3);
        applied.SelectMany(p => p.Ids).Should().BeEquivalentTo(quotes.Select(q => q.Id));
        foreach (var q in quotes)
        {
            var s = await ReadAsync(factory, q.Id);
            s.Status.Should().Be(DocumentStatus.Archived);
            s.Version.Should().Be(before[q.Id].Version + 1);
            s.LastNote.Should().Be(ArchiveAstQuotesEndpoint.ChangeNote);
        }
        foreach (var d in untouched.Append(alreadyArchived))
            (await ReadAsync(factory, d.Id)).Should().BeEquivalentTo(before[d.Id], "対象外は動かさない");

        var events = Published(factory);
        events.Select(e => e.DocumentId).Should().BeEquivalentTo(quotes.Select(q => q.Id));
        events.Should().OnlyContain(e => e.Status == DocumentStatus.Archived);
        factory.Storage.GetTextCallCount.Should().Be(reads, "本文は読まない");
    }

    [Fact]
    public async Task 二回流しても二回目は何もせず_版もイベントも増えない_同じ作成時刻もちょうど一回ずつ辿る()
    {
        await using var factory = new TestWebApplicationFactory();
        var at = DateTimeOffset.UtcNow.AddDays(-3);
        var quotes = new List<Document>();
        for (var i = 0; i < 7; i++) quotes.Add(await SeedAsync(factory, createdAt: i < 4 ? at : at.AddMinutes(i)));
        for (var i = 0; i < 3; i++) await SeedAsync(factory, kind: "News", createdAt: at);
        var admin = Admin(factory);

        var first = await RunAllAsync(admin, dryRun: false, limit: 3);
        first.Sum(p => p.Archived).Should().Be(7);
        first.SelectMany(p => p.Ids).Should().OnlyHaveUniqueItems().And.HaveCount(7);
        var versions = new Dictionary<Guid, int>();
        foreach (var q in quotes) versions[q.Id] = (await ReadAsync(factory, q.Id)).Version;
        var published = Published(factory).Count;

        var second = await RunAllAsync(admin, dryRun: false, limit: 3);
        second.Sum(p => p.Archived).Should().Be(0);
        foreach (var q in quotes) (await ReadAsync(factory, q.Id)).Version.Should().Be(versions[q.Id]);
        Published(factory).Count.Should().Be(published);
    }
}
