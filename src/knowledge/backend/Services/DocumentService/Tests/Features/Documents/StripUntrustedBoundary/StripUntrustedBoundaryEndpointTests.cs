using System.Net;
using System.Net.Http.Json;
using Amazon.S3;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.StripUntrustedBoundary;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.StripUntrustedBoundary;

// FR-06, UC-03, [[IADR-0555]] (#1973): **AST が境界語つきで保存した文書から枠を剥がす口（`POST /documents/strip-untrusted-boundary`）。**
//
// 🔴 **対象外を絶対に触らない**ことを固定する: 枠の無い AST の文書・他の project・個人資料・アーカイブ済み。
//   触らないとは「表題・本文の参照・版・更新時刻が動かず、イベントも出ない」ことである。
// 🔴 **変異試験の対象である**（作業仕様書 §変異試験）: `AdminOnly` を外す・dry-run でも書き換える・アーカイブ済みを母集合から外さない・
//   project の絞り込みを外す・枠の判定を反転する・読めないときもページの途中まで書き換える、のいずれでも落ちる。
//
// 件数を正確に数えるため、**試験ごとに器（＝ InMemory の DB と記録用ストレージ）を作る**。
[Trait("TestKind", "Integration")]
public class StripUntrustedBoundaryEndpointTests
{
    private const string Path = "/documents/strip-untrusted-boundary";
    private const string Open = UntrustedBoundaryFrame.OpenMarker;
    private const string Close = UntrustedBoundaryFrame.CloseMarker;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Wrap(string s) => $"{Open}\n{s}\n{Close}";

    private static HttpClient ClientAs(TestWebApplicationFactory factory, string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static HttpClient Admin(TestWebApplicationFactory factory) => ClientAs(factory, "admin-1", "platform-admin");

    // 台帳へ直接置く。`body` が null なら本文の参照を持たない文書、`bodyMissing` なら参照はあるがオブジェクトが無い（NoSuchKey）文書。
    private static async Task<Document> SeedAsync(TestWebApplicationFactory factory, string title, string? body,
        string? project = "ai-stock-trading", bool privateNote = false, bool archived = false, bool bodyMissing = false,
        DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var id = Guid.NewGuid();
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal", ["kind"] = "News" };
        if (project is not null) attributes["project"] = project;
        if (privateNote)
        {
            attributes[DocumentScopes.Key] = DocumentScopes.PrivateNote;
            attributes["owner"] = "alice";
        }

        Document doc;
        if (body is null && !bodyMissing)
        {
            doc = Document.Create(title, "https://example.com/news/1", "text/html", attributes);
        }
        else
        {
            var uri = $"storage://{RecordingObjectStorageClient.Bucket}/documents/{id:D}/body.md";
            doc = Document.CreateWithBody(id, title, uri, null, "text/markdown", attributes);
            if (!bodyMissing) factory.Storage.Texts[uri] = body!;
        }
        if (archived) doc.Archive();
        db.Documents.Add(doc);
        if (createdAt is { } at) db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = at;
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private sealed record Snapshot(string Title, string Status, string? MarkdownUri, int Version, DateTimeOffset UpdatedAt,
        int Versions, string? LastNote, string? Fingerprint, string? PreviousUri, IReadOnlyDictionary<string, string> Attributes);

    private static async Task<Snapshot> ReadAsync(TestWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var d = await db.Documents.AsNoTracking().Include(x => x.Versions).SingleAsync(x => x.Id == id, Ct);
        var versions = d.Versions.OrderBy(v => v.Version).ToList();
        return new Snapshot(d.Title, d.Status, d.MarkdownUri, d.Version, d.UpdatedAt, versions.Count, versions[^1].ChangeNote,
            d.ContentFingerprint, versions.Count > 1 ? versions[^2].MarkdownUri : null, d.Attributes);
    }

    private static IReadOnlyList<DocumentUpdated> Published(TestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>();

    private static object Body_(bool dryRun, int? limit = null, string? cursor = null, string? reason = "AST の境界語を剥がす")
        => new { dryRun, limit, cursor, requestedBy = "alice", reason };

    private static async Task<StripUntrustedBoundaryResponse> CallAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync(Path, body, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<StripUntrustedBoundaryResponse>(Ct))!;
    }

    private static async Task<List<StripUntrustedBoundaryResponse>> RunAllAsync(HttpClient client, bool dryRun, int limit)
    {
        var pages = new List<StripUntrustedBoundaryResponse>();
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
    public async Task 管理者だけが呼べ_運用者の人とAST_の書き手と一般の利用者は403で何も書き換えない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, Wrap("見出し"), Wrap("本文"));
        var body = Body_(dryRun: false);

        (await ClientAs(factory, "operator-1", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "service-account-ai-stock-trading-kb-writer", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "user-1", "platform-user").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync(factory, doc.Id)).Title.Should().Be(Wrap("見出し"));
        Published(factory).Should().BeEmpty();

        // 陽性対照: 同じ要求を管理者が送れば剥がす。
        (await CallAsync(Admin(factory), body)).Stripped.Should().Be(1);
        (await ReadAsync(factory, doc.Id)).Title.Should().Be("見出し");
    }

    [Fact]
    public void 口は_AdminOnly_を積む()
    {
        using var factory = new TestWebApplicationFactory();
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.EndsWith(StripUntrustedBoundaryEndpoint.Route, StringComparison.Ordinal) == true);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
            .Should().Contain(PlatformAuthPolicies.AdminOnly);
    }

    // ── 必須の指定 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"limit":10}""", "dryRun")]
    [InlineData("""{"dryRun":false}""", "reason")]
    [InlineData("""{"dryRun":false,"reason":"  "}""", "reason")]
    [InlineData("""{"dryRun":true,"limit":0}""", "limit")]
    [InlineData("""{"dryRun":true,"limit":201}""", "limit")]
    [InlineData("""{"dryRun":true,"cursor":"not-a-cursor"}""", "cursor")]
    public async Task 必須の指定が無い_範囲外_不正なカーソルは400でその鍵を返し何も読まない(string json, string key)
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, Wrap("見出し"), Wrap("本文"));
        var reads = factory.Storage.GetTextCallCount;

        var resp = await Admin(factory).PostAsync(Path,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"{key}\"");
        factory.Storage.GetTextCallCount.Should().Be(reads);
        (await ReadAsync(factory, doc.Id)).Title.Should().Be(Wrap("見出し"));
    }

    // ── 対象だけを剥がす ──────────────────────────────────────────────────────

    [Fact]
    public async Task dry_run_は件数と対象のIDだけを返し_本番は枠のある_AST_の文書だけを剥がし_対象外には触らない()
    {
        await using var factory = new TestWebApplicationFactory();
        var both = await SeedAsync(factory, Wrap("トヨタ 決算"), Wrap("営業利益が増えた。"));
        var titleOnly = await SeedAsync(factory, Wrap("表題だけ"), null);
        var titleOnlyMissing = await SeedAsync(factory, Wrap("本文が無い"), null, bodyMissing: true);
        var plain = await SeedAsync(factory, "素の表題", "素の本文");
        var otherProject = await SeedAsync(factory, Wrap("他の project"), Wrap("本文"), project: "other");
        var noProject = await SeedAsync(factory, Wrap("project なし"), Wrap("本文"), project: null);
        var privateNote = await SeedAsync(factory, Wrap("個人資料"), Wrap("本文"), privateNote: true);
        var archived = await SeedAsync(factory, Wrap("アーカイブ済み"), Wrap("本文"), archived: true);
        var untouched = new[] { plain, otherProject, noProject, privateNote, archived };
        var before = new Dictionary<Guid, Snapshot>();
        foreach (var d in untouched.Append(both).Append(titleOnly).Append(titleOnlyMissing))
            before[d.Id] = await ReadAsync(factory, d.Id);
        // 実クライアントは無いキーを NoSuchKey の例外で返す（記録用ストレージの既定は KeyNotFoundException）。
        factory.Storage.GetTextThrows = uri => factory.Storage.Texts.ContainsKey(uri)
            ? null
            : new AmazonS3Exception("simulated") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
        var admin = Admin(factory);

        // dry-run: 件数と ID だけ。何も動かさない。
        var dry = await RunAllAsync(admin, dryRun: true, limit: 2);
        dry.Sum(p => p.Wrapped).Should().Be(3);
        dry.Sum(p => p.TitleWrapped).Should().Be(3);
        dry.Sum(p => p.BodyWrapped).Should().Be(1);
        dry.Sum(p => p.BodyMissing).Should().Be(1);
        dry.Sum(p => p.NotStored).Should().Be(1);
        dry.Sum(p => p.Stripped).Should().Be(0);
        dry.Sum(p => p.Selected).Should().Be(4, "母集合は project = ai-stock-trading の未アーカイブの組織文書 4 件");
        dry.SelectMany(p => p.Items).Select(i => i.Id).Should().BeEquivalentTo([both.Id, titleOnly.Id, titleOnlyMissing.Id]);
        dry.SelectMany(p => p.Items).Single(i => i.Id == both.Id).Should().Be(new StripUntrustedBoundaryItem(both.Id, true, true));
        foreach (var (id, snap) in before)
            (await ReadAsync(factory, id)).Should().BeEquivalentTo(snap, "dry-run は何も動かさない");
        factory.Storage.PutTextCallCount.Should().Be(0);
        Published(factory).Should().BeEmpty();

        // 本番。
        var applied = await RunAllAsync(admin, dryRun: false, limit: 2);
        applied.Sum(p => p.Stripped).Should().Be(3);

        var b = await ReadAsync(factory, both.Id);
        b.Title.Should().Be("トヨタ 決算");
        b.MarkdownUri.Should().EndWith($"documents/{both.Id:D}/body-boundary-stripped.md");
        factory.Storage.Texts[b.MarkdownUri!].Should().Be("営業利益が増えた。");
        b.Fingerprint.Should().Be(DocumentBodyIntake.Fingerprint("営業利益が増えた。"));
        b.Version.Should().Be(before[both.Id].Version + 1, "表題と本文を同じ版で変える");
        b.LastNote.Should().Be(StripUntrustedBoundaryEndpoint.ChangeNote);
        b.Status.Should().Be(before[both.Id].Status, "状態は動かさない");
        b.Attributes.Should().BeEquivalentTo(before[both.Id].Attributes, "属性は動かさない");
        b.PreviousUri.Should().Be(before[both.Id].MarkdownUri, "前の版は元の本文を指したまま");
        factory.Storage.Texts[before[both.Id].MarkdownUri!].Should().Be(Wrap("営業利益が増えた。"), "元のオブジェクトは上書きしない");

        var t = await ReadAsync(factory, titleOnly.Id);
        t.Title.Should().Be("表題だけ");
        t.MarkdownUri.Should().BeNull();
        var m = await ReadAsync(factory, titleOnlyMissing.Id);
        m.Title.Should().Be("本文が無い");
        m.MarkdownUri.Should().Be(before[titleOnlyMissing.Id].MarkdownUri, "本文が無いときは参照を動かさない");
        m.Fingerprint.Should().Be(before[titleOnlyMissing.Id].Fingerprint);

        foreach (var d in untouched)
            (await ReadAsync(factory, d.Id)).Should().BeEquivalentTo(before[d.Id], "対象外は動かさない");

        var events = Published(factory);
        events.Select(e => e.DocumentId).Should().BeEquivalentTo([both.Id, titleOnly.Id, titleOnlyMissing.Id]);
        events.Single(e => e.DocumentId == both.Id).Title.Should().Be("トヨタ 決算");
        events.Single(e => e.DocumentId == both.Id).MarkdownUri.Should().Be(b.MarkdownUri);
    }

    [Fact]
    public async Task 二回流しても二回目は何も変えず_版もイベントも増えない_同じ作成時刻もちょうど一回ずつ辿る()
    {
        await using var factory = new TestWebApplicationFactory();
        var at = DateTimeOffset.UtcNow.AddDays(-3);
        var docs = new List<Document>();
        for (var i = 0; i < 7; i++)
            docs.Add(await SeedAsync(factory, Wrap($"見出し {i}"), Wrap($"本文 {i}"), createdAt: i < 4 ? at : at.AddMinutes(i)));
        for (var i = 0; i < 3; i++)
            await SeedAsync(factory, $"素の {i}", $"素の本文 {i}", createdAt: at);
        var admin = Admin(factory);

        var first = await RunAllAsync(admin, dryRun: false, limit: 3);
        first.Sum(p => p.Stripped).Should().Be(7);
        first.Sum(p => p.Selected).Should().Be(10, "取りこぼしも重複も無い");
        var versions = new Dictionary<Guid, int>();
        foreach (var d in docs) versions[d.Id] = (await ReadAsync(factory, d.Id)).Version;
        var published = Published(factory).Count;
        var puts = factory.Storage.PutTextCallCount;

        var second = await RunAllAsync(admin, dryRun: false, limit: 3);
        second.Sum(p => p.Wrapped).Should().Be(0);
        second.Sum(p => p.Stripped).Should().Be(0);
        foreach (var d in docs) (await ReadAsync(factory, d.Id)).Version.Should().Be(versions[d.Id]);
        Published(factory).Count.Should().Be(published);
        factory.Storage.PutTextCallCount.Should().Be(puts);
    }

    [Theory]
    [InlineData("<<<UNTRUSTED_DATA\nA\nUNTRUSTED_DATA>>>", "A")]
    [InlineData("<<<UNTRUSTED_DATA\r\nA\r\nUNTRUSTED_DATA>>>", "A")]
    [InlineData("<<<UNTRUSTED_DATAAUNTRUSTED_DATA>>>", "A")]
    [InlineData("<<<UNTRUSTED_DATA\n<<<UNTRUSTED_DATA\nA\nUNTRUSTED_DATA>>>\nUNTRUSTED_DATA>>>", "A")]
    [InlineData("<<<UNTRUSTED_DATA\n\n行 1\n\n行 2\n\nUNTRUSTED_DATA>>>", "\n行 1\n\n行 2\n")]
    [InlineData("<<<UNTRUSTED_DATA\nA UNTRUSTED_DATA>>> B\nUNTRUSTED_DATA>>>", "A UNTRUSTED_DATA>>> B")]
    [InlineData("A\n<<<UNTRUSTED_DATA\nB\nUNTRUSTED_DATA>>>", "A\n<<<UNTRUSTED_DATA\nB\nUNTRUSTED_DATA>>>")]
    [InlineData("<<<UNTRUSTED_DATA\nB\nUNTRUSTED_DATA>>>\n", "<<<UNTRUSTED_DATA\nB\nUNTRUSTED_DATA>>>\n")]
    [InlineData("<<<UNTRUSTED_DATA>>>", "<<<UNTRUSTED_DATA>>>")]
    [InlineData("素の文字列", "素の文字列")]
    public void 枠は先頭と末尾の境界語と隣の改行1つだけを_枠が無くなるまで剥がし_中身と枠でない値には触らない(string input, string expected)
    {
        UntrustedBoundaryFrame.Strip(input).Should().Be(expected);
        UntrustedBoundaryFrame.IsWrapped(UntrustedBoundaryFrame.Strip(input)).Should().BeFalse("出力は枠の形をしない（冪等の根拠）");
        UntrustedBoundaryFrame.Strip(UntrustedBoundaryFrame.Strip(input)).Should().Be(expected);
        UntrustedBoundaryFrame.IsWrapped(input).Should().Be(input != expected);
        UntrustedBoundaryFrame.Strip(null).Should().BeNull();
    }

    [Fact]
    public async Task 剥がすと空になる表題は剥がさず数え_本文だけを剥がす()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, $"{Open}\n\n{Close}", Wrap("本文"));

        var r = await CallAsync(Admin(factory), Body_(dryRun: false));

        r.TitleLeftWrapped.Should().Be(1);
        r.Stripped.Should().Be(1);
        var s = await ReadAsync(factory, doc.Id);
        s.Title.Should().Be($"{Open}\n\n{Close}");
        factory.Storage.Texts[s.MarkdownUri!].Should().Be("本文");
    }

    // ── 読めない・書けないときは何もしない ────────────────────────────────────

    [Fact]
    public async Task 本文が_NoSuchKey_以外で読めないとそのページは何も書き換えず502_直せば同じ要求で剥がす_未構成は503()
    {
        await using var factory = new TestWebApplicationFactory();
        var first = await SeedAsync(factory, Wrap("1 件目"), Wrap("本文 1"), createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        var second = await SeedAsync(factory, Wrap("2 件目"), Wrap("本文 2"), createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        factory.Storage.GetTextThrows = uri => uri == second.MarkdownUri
            ? new AmazonS3Exception("simulated") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchBucket" }
            : null;

        var resp = await Admin(factory).PostAsJsonAsync(Path, Body_(dryRun: false), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadAsync(factory, first.Id)).Title.Should().Be(Wrap("1 件目"), "読めた 1 件目も書き換えない");
        factory.Storage.PutTextCallCount.Should().Be(0);
        Published(factory).Should().BeEmpty();

        factory.Storage.GetTextThrows = _ => new NotSupportedException("Object storage is not configured");
        (await Admin(factory).PostAsJsonAsync(Path, Body_(dryRun: false), Ct)).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable);

        factory.Storage.GetTextThrows = null;
        (await CallAsync(Admin(factory), Body_(dryRun: false))).Stripped.Should().Be(2);
    }

    [Fact]
    public async Task 剥がした本文を書けないとそのページは台帳を保存せず502_次の実行で剥がす()
    {
        await using var factory = new TestWebApplicationFactory();
        var first = await SeedAsync(factory, Wrap("1 件目"), Wrap("本文 1"), createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        var second = await SeedAsync(factory, Wrap("2 件目"), Wrap("本文 2"), createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        var before = await ReadAsync(factory, first.Id);
        factory.Storage.PutTextThrows = key => key.Contains(second.Id.ToString("D"), StringComparison.Ordinal)
            ? new AmazonS3Exception("simulated") { StatusCode = HttpStatusCode.InternalServerError }
            : null;

        var resp = await Admin(factory).PostAsJsonAsync(Path, Body_(dryRun: false), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadAsync(factory, first.Id)).Should().BeEquivalentTo(before, "書けた 1 件目も台帳は動かさない");
        Published(factory).Should().BeEmpty();

        factory.Storage.PutTextThrows = null;
        (await CallAsync(Admin(factory), Body_(dryRun: false))).Stripped.Should().Be(2);
        (await ReadAsync(factory, first.Id)).Title.Should().Be("1 件目");
    }
}
