using System.Net;
using System.Net.Http.Json;
using Amazon.S3;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.ArchiveBodyless;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.ArchiveBodyless;

// FR-06, UC-03, SC-05, [[IADR-0549]] (#1931 項目 3。オーナー裁定 B):
// **本文のオブジェクトが無い（NoSuchKey）古い文書の一括アーカイブの口（`POST /documents/archive-bodyless`）。**
//
// 🔴 **対象外を絶対に触らない**ことを固定する: 本文が在る・切り替え後（`createdAt ≥ createdBefore`）・本文の参照が無い・
//   個人資料・アーカイブ済み。触らないとは「状態・版・更新時刻が動かず、イベントも出ない」ことである。
// 🔴 **変異試験の対象である**（作業仕様書 §変異試験）: `AdminOnly` を外す・期限を「以下」にする・存在確認の結果を反転する・
//   dry-run でもアーカイブする・アーカイブ済みを母集合から外さない・確かめの失敗でもページの途中まで保存する、のいずれでも落ちる。
//
// 件数を正確に数えるため、**試験ごとに器（＝ InMemory の DB と記録用ストレージ）を作る**。
[Trait("TestKind", "Integration")]
public class ArchiveBodylessDocumentsEndpointTests
{
    private const string Path = "/documents/archive-bodyless";
    private static readonly DateTimeOffset Cutover = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient ClientAs(TestWebApplicationFactory factory, string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static HttpClient Admin(TestWebApplicationFactory factory) => ClientAs(factory, "admin-1", "platform-admin");

    private enum Body { Missing, Present, NotStored }

    // 台帳へ直接置く。`Missing` は storage:// の参照を持つが記録用ストレージにオブジェクトが無い（＝ NoSuchKey）文書。
    private static async Task<Document> SeedAsync(TestWebApplicationFactory factory, string title, DateTimeOffset createdAt,
        Body body = Body.Missing, bool privateNote = false, bool archived = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var id = Guid.NewGuid();
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (privateNote)
        {
            attributes[DocumentScopes.Key] = DocumentScopes.PrivateNote;
            attributes["owner"] = "alice";
        }

        Document doc;
        if (body == Body.NotStored)
        {
            doc = Document.Create(title, "https://example.com/news/1", "text/html", attributes);
        }
        else
        {
            var uri = $"storage://{RecordingObjectStorageClient.Bucket}/documents/{id:N}/body.md";
            doc = Document.CreateWithBody(id, title, uri, null, "text/markdown", attributes);
            if (body == Body.Present) factory.Storage.Texts[uri] = $"# {title}";
        }
        if (archived) doc.Archive();
        db.Documents.Add(doc);
        db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = createdAt;
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private sealed record Snapshot(string Status, int Version, DateTimeOffset UpdatedAt, int Versions, string? LastNote);

    private static async Task<Snapshot> ReadAsync(TestWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var d = await db.Documents.AsNoTracking().Include(x => x.Versions).SingleAsync(x => x.Id == id, Ct);
        return new Snapshot(d.Status, d.Version, d.UpdatedAt, d.Versions.Count, d.Versions.Last().ChangeNote);
    }

    private static IReadOnlyList<DocumentUpdated> Published(TestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>();

    private static object Body_(bool dryRun, int? limit = null, string? cursor = null, DateTimeOffset? before = null,
        string? reason = "本文の無い切り替え前の文書をアーカイブ")
        => new { dryRun, createdBefore = before ?? Cutover, limit, cursor, requestedBy = "alice", reason };

    private static async Task<ArchiveBodylessDocumentsResponse> CallAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync(Path, body, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<ArchiveBodylessDocumentsResponse>(Ct))!;
    }

    // 1 ページずつ辿って全件を流す（駆動スクリプトの動き）。
    private static async Task<List<ArchiveBodylessDocumentsResponse>> RunAllAsync(HttpClient client, bool dryRun, int limit)
    {
        var pages = new List<ArchiveBodylessDocumentsResponse>();
        string? cursor = null;
        do
        {
            var r = await CallAsync(client, Body_(dryRun, limit, cursor));
            pages.Add(r);
            cursor = r.NextCursor;
        } while (cursor is not null && pages.Count < 50);
        return pages;
    }

    // ── 管理者だけ ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 管理者だけが呼べ_運用者の人とAST_の書き手と一般の利用者は403で何もアーカイブしない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, "old", Cutover.AddDays(-30));
        var body = Body_(dryRun: false);

        (await ClientAs(factory, "operator-1", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "service-account-ai-stock-trading-kb-writer", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "user-1", "platform-user").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync(factory, doc.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        Published(factory).Should().BeEmpty();

        // 陽性対照: 同じ要求を管理者が送ればアーカイブされる（拒否が「口が無い」ことの見かけでないことを示す）。
        (await CallAsync(Admin(factory), body)).Archived.Should().Be(1);
        (await ReadAsync(factory, doc.Id)).Status.Should().Be(DocumentStatus.Archived);
    }

    [Fact]
    public async Task 口はAdminOnlyを積む()
    {
        await using var factory = new TestWebApplicationFactory();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/documents" + ArchiveBodylessDocumentsEndpoint.Route);

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Should().Contain(a => a.Policy == PlatformAuthPolicies.AdminOnly);
    }

    // ── 要求の検証 ────────────────────────────────────────────────────────────

    public static TheoryData<string, string> BadRequests => new()
    {
        { """{"createdBefore":"2026-09-27T00:00:00Z"}""", "dryRun" },
        { """{"dryRun":true}""", "createdBefore" },
        { """{"dryRun":false,"createdBefore":"2026-09-27T00:00:00Z"}""", "reason" },
        { """{"dryRun":false,"createdBefore":"2026-09-27T00:00:00Z","reason":"   "}""", "reason" },
        { """{"dryRun":true,"createdBefore":"2026-09-27T00:00:00Z","limit":0}""", "limit" },
        { """{"dryRun":true,"createdBefore":"2026-09-27T00:00:00Z","limit":201}""", "limit" },
        { """{"dryRun":true,"createdBefore":"2026-09-27T00:00:00Z","cursor":"not-a-cursor"}""", "cursor" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task 必須の指定が欠けた要求と不正な要求は400で_何もアーカイブしない(string json, string key)
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, "old", Cutover.AddDays(-30));

        var resp = await Admin(factory).PostAsync(Path,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"{key}\"");
        (await ReadAsync(factory, doc.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        factory.Storage.ExistsCalls.Should().BeEmpty("検証で落とした要求はストレージにも当たらない");
    }

    // ── 対象と対象外 ──────────────────────────────────────────────────────────

    private sealed record World(
        List<Document> Targets, Document Present, Document AtCutover, Document AfterCutover,
        Document NotStored, Document PrivateNote, Document AlreadyArchived)
    {
        public IEnumerable<Document> Untouchables => [Present, AtCutover, AfterCutover, NotStored, PrivateNote, AlreadyArchived];
    }

    private static async Task<World> SeedWorldAsync(TestWebApplicationFactory factory)
    {
        var t = Cutover.AddDays(-20);
        var targets = new List<Document>();
        for (var i = 0; i < 3; i++) targets.Add(await SeedAsync(factory, $"old-missing-{i}", t.AddHours(i)));
        return new World(
            targets,
            Present: await SeedAsync(factory, "old-present", t.AddMinutes(30), Body.Present),
            // 🔴 期限ちょうどは対象外（`createdAt < createdBefore`）。本文が無くても触らない。
            AtCutover: await SeedAsync(factory, "at-cutover-missing", Cutover),
            AfterCutover: await SeedAsync(factory, "after-cutover-missing", Cutover.AddDays(1)),
            NotStored: await SeedAsync(factory, "old-not-stored", t.AddMinutes(10), Body.NotStored),
            PrivateNote: await SeedAsync(factory, "old-private-missing", t.AddMinutes(20), privateNote: true),
            AlreadyArchived: await SeedAsync(factory, "old-archived-missing", t.AddMinutes(40), archived: true));
    }

    [Fact]
    public async Task dry_runは件数と内訳だけを返し_台帳もイベントも動かさず_本文を読まない()
    {
        await using var factory = new TestWebApplicationFactory();
        var w = await SeedWorldAsync(factory);
        var before = new Dictionary<Guid, Snapshot>();
        foreach (var d in w.Targets.Concat(w.Untouchables)) before[d.Id] = await ReadAsync(factory, d.Id);

        var pages = await RunAllAsync(Admin(factory), dryRun: true, limit: 2);

        pages.Should().AllSatisfy(p => p.DryRun.Should().BeTrue());
        pages[0].Matched.Should().Be(5, "期限より前・未アーカイブ・storage:// の参照 = 本文なし 3 ＋ 本文あり 1 ＋ 個人資料 1");
        pages[0].AlreadyArchived.Should().Be(1);
        pages[0].NotStored.Should().Be(1);
        pages.Sum(p => p.BodyMissing).Should().Be(3);
        pages.Sum(p => p.BodyPresent).Should().Be(1);
        pages.Sum(p => p.SkippedPrivateNote).Should().Be(1);
        pages.Sum(p => p.Archived).Should().Be(0);
        pages[^1].NextCursor.Should().BeNull();

        foreach (var d in w.Targets.Concat(w.Untouchables))
            (await ReadAsync(factory, d.Id)).Should().Be(before[d.Id], $"dry-run は {d.Title} を動かさない");
        Published(factory).Should().BeEmpty();
        factory.Storage.GetTextCallCount.Should().Be(0, "存在確認は本文を読まない");
    }

    [Fact]
    public async Task 本文の無い切り替え前の文書だけをアーカイブし_対象外は状態も版も更新時刻も動かさない()
    {
        await using var factory = new TestWebApplicationFactory();
        var w = await SeedWorldAsync(factory);
        var before = new Dictionary<Guid, Snapshot>();
        foreach (var d in w.Untouchables) before[d.Id] = await ReadAsync(factory, d.Id);

        var pages = await RunAllAsync(Admin(factory), dryRun: false, limit: 2);

        pages.Sum(p => p.Archived).Should().Be(3);
        foreach (var d in w.Targets)
        {
            var s = await ReadAsync(factory, d.Id);
            s.Status.Should().Be(DocumentStatus.Archived);
            s.LastNote.Should().Be(ArchiveBodylessDocumentsEndpoint.ChangeNote, "どの版がこの操作で作られたかを台帳から引ける（戻し方の目印）");
            s.Versions.Should().Be(2, "作成の版 ＋ アーカイブの版");
        }
        foreach (var d in w.Untouchables)
            (await ReadAsync(factory, d.Id)).Should().Be(before[d.Id], $"{d.Title} は対象外であり触らない");

        // 下流（取り込み・Wiki 同期）へは単票のアーカイブと同じイベントで伝える。対象外へは 1 件も出さない。
        var events = Published(factory);
        events.Select(e => e.DocumentId).Should().BeEquivalentTo(w.Targets.Select(d => d.Id));
        events.Should().AllSatisfy(e => e.Status.Should().Be(DocumentStatus.Archived));
        factory.Storage.GetTextCallCount.Should().Be(0, "存在確認は本文を読まない");
        factory.Storage.ExistsCalls.Should().NotContain(w.NotStored.MarkdownUri ?? "(null)")
            .And.NotContain(w.AtCutover.MarkdownUri!).And.NotContain(w.AfterCutover.MarkdownUri!)
            .And.NotContain(w.AlreadyArchived.MarkdownUri!).And.NotContain(w.PrivateNote.MarkdownUri!,
                "母集合の外と個人資料はストレージにも当たらない");
    }

    [Fact]
    public async Task 二回流しても同じで_二回目は何もアーカイブせずイベントも出さない()
    {
        await using var factory = new TestWebApplicationFactory();
        var w = await SeedWorldAsync(factory);
        var admin = Admin(factory);

        await RunAllAsync(admin, dryRun: false, limit: 2);
        var afterFirst = new Dictionary<Guid, Snapshot>();
        foreach (var d in w.Targets.Concat(w.Untouchables)) afterFirst[d.Id] = await ReadAsync(factory, d.Id);
        var eventsAfterFirst = Published(factory).Count;

        var second = await RunAllAsync(admin, dryRun: false, limit: 2);

        second.Sum(p => p.Archived).Should().Be(0);
        second[0].Matched.Should().Be(2, "残るのは本文の在る文書と個人資料だけ");
        second[0].AlreadyArchived.Should().Be(4, "1 回目の 3 件 ＋ 元からの 1 件");
        foreach (var d in w.Targets.Concat(w.Untouchables))
            (await ReadAsync(factory, d.Id)).Should().Be(afterFirst[d.Id], $"2 回目は {d.Title} を動かさない（版を積まない）");
        Published(factory).Should().HaveCount(eventsAfterFirst);
    }

    [Fact]
    public async Task ページを辿ると本文の無い文書がちょうど1回ずつアーカイブされ_最後のページでカーソルが尽きる()
    {
        await using var factory = new TestWebApplicationFactory();
        var t0 = Cutover.AddDays(-10);
        var missing = new List<Document>();
        // 同じ作成時刻を混ぜ、本文の在る文書を間に挟む（キーセットの境目で取りこぼし・重複が無いこと）。
        for (var i = 0; i < 7; i++)
        {
            missing.Add(await SeedAsync(factory, $"m{i}", t0.AddMinutes(i / 2)));
            if (i % 3 == 0) await SeedAsync(factory, $"p{i}", t0.AddMinutes(i / 2), Body.Present);
        }

        var pages = await RunAllAsync(Admin(factory), dryRun: false, limit: 3);

        pages.Should().HaveCount(4, "本文なし 7 ＋ 本文あり 3 = 10 件を 3 件ずつ");
        pages.Sum(p => p.Archived).Should().Be(7);
        pages.Sum(p => p.BodyPresent).Should().Be(3);
        pages.Select(p => p.Remaining).Should().BeEquivalentTo(new[] { 10, 7, 4, 1 }, o => o.WithStrictOrdering());
        Published(factory).Select(e => e.DocumentId).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(missing.Select(d => d.Id));
    }

    // ── 「無い」と言えないときは何もしない ─────────────────────────────────────

    [Fact]
    public async Task 確かめに失敗したらそのページは1件もアーカイブせず_失敗として返す()
    {
        await using var factory = new TestWebApplicationFactory();
        var first = await SeedAsync(factory, "a", Cutover.AddDays(-3));
        var second = await SeedAsync(factory, "b", Cutover.AddDays(-2));
        // 2 件目の確かめでバケットが無い（構成の不調）。1 件目は NoSuchKey と確かめ済みでも保存しない。
        factory.Storage.ExistsThrows = uri => uri == second.MarkdownUri
            ? new AmazonS3Exception("simulated") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchBucket" }
            : null;

        var failed = false;
        try
        {
            var resp = await Admin(factory).PostAsJsonAsync(Path, Body_(dryRun: false), Ct);
            failed = !resp.IsSuccessStatusCode;
        }
        catch (AmazonS3Exception)
        {
            failed = true; // 器が例外をそのまま運ぶ形でも、失敗として届けばよい。
        }

        failed.Should().BeTrue("確かめられないまま成功を返さない");
        (await ReadAsync(factory, first.Id)).Status.Should().NotBe(DocumentStatus.Archived, "ページの途中まで保存しない");
        (await ReadAsync(factory, second.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        Published(factory).Should().BeEmpty();

        // 原因が直れば、同じ要求でページごとアーカイブされる（再実行で回収できる）。
        factory.Storage.ExistsThrows = null;
        (await CallAsync(Admin(factory), Body_(dryRun: false))).Archived.Should().Be(2);
    }

    [Fact]
    public async Task ストレージが構成されていなければ503で何もアーカイブしない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, "a", Cutover.AddDays(-3));
        factory.Storage.ExistsThrows = _ => new NotSupportedException("Object storage is not configured");

        var resp = await Admin(factory).PostAsJsonAsync(Path, Body_(dryRun: false), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ReadAsync(factory, doc.Id)).Status.Should().NotBe(DocumentStatus.Archived);
        Published(factory).Should().BeEmpty();
    }
}
