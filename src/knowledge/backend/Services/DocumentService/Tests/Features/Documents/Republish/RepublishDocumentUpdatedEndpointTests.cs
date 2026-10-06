using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.Republish;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.Republish;

// FR-02, FR-06, UC-04, ADR-0013, ADR-0027, [[IADR-0503]] (#1762):
// **`DocumentUpdated` の再発行の口（`POST /documents/republish-updated`）。管理者だけ・ページ・カーソル・dry-run。**
//
// 件数を正確に数えるため、**試験ごとに器（＝ InMemory の DB）を作る**（クラスで器を共有すると他の試験の文書が混ざる）。
//
// 🔴 **変異試験の対象である**（作業仕様書 §変異試験）: `AdminOnly` を外す・カーソルの比較を「以上」にする・
//   次のカーソルをページの先頭から作る・dry-run でも発行する・`dryRun` の省略を発行に倒す・門を通さない、のいずれでも落ちる。
[Trait("TestKind", "Integration")]
public class RepublishDocumentUpdatedEndpointTests
{
    private const string Path = "/documents/republish-updated";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient ClientAs(TestWebApplicationFactory factory, string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static HttpClient Admin(TestWebApplicationFactory factory) => ClientAs(factory, "admin-1", "platform-admin");

    private static Dictionary<string, string> Attrs(string confidentiality, params (string Key, string Value)[] extra)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = confidentiality };
        foreach (var (k, v) in extra) attributes[k] = v;
        return attributes;
    }

    // 本文つきの文書（`MarkdownUri` が立つ）を台帳へ直接置く。作成時刻を指定したいときは `createdAt` を渡す。
    private static async Task<Document> SeedAsync(TestWebApplicationFactory factory, string title,
        Dictionary<string, string> attributes, bool withBody = true, DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var id = Guid.NewGuid();
        var doc = withBody
            ? Document.CreateWithBody(id, title, $"storage://documents/{id:N}/body.md", null, "text/markdown", attributes)
            : Document.Create(title, null, "text/markdown", attributes);
        db.Documents.Add(doc);
        if (createdAt is { } at) db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = at;
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static IReadOnlyList<DocumentUpdated> Published(TestWebApplicationFactory factory)
        => factory.Services.GetRequiredService<RecordingMessageBus>().PublishedOf<DocumentUpdated>();

    private static async Task<RepublishDocumentUpdatedResponse> CallAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync(Path, body, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<RepublishDocumentUpdatedResponse>(Ct))!;
    }

    // ── AC-1: 管理者だけ ─────────────────────────────────────────────────────

    [Fact]
    public async Task 管理者だけが呼べ_運用者の人とAST_の書き手と一般の利用者は403で何も発行しない()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, "a", Attrs("internal"));
        var body = new { dryRun = false };

        (await ClientAs(factory, "operator-1", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "service-account-ai-stock-trading-kb-writer", "platform-operator").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "user-1", "platform-user").PostAsJsonAsync(Path, body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Published(factory).Should().BeEmpty("拒否した呼び出しは 1 件も発行しない");

        // 陽性対照: 同じ要求を管理者が送れば発行される（拒否が「口が無い」ことの見かけでないことを示す）。
        (await CallAsync(Admin(factory), body)).Published.Should().Be(1);
        Published(factory).Should().HaveCount(1);
    }

    [Fact]
    public async Task 口はAdminOnlyを積む()
    {
        await using var factory = new TestWebApplicationFactory();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/documents" + RepublishDocumentUpdatedEndpoint.Route);

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Should().Contain(a => a.Policy == PlatformAuthPolicies.AdminOnly);
    }

    // ── AC-2: ページとカーソル ───────────────────────────────────────────────

    [Fact]
    public async Task ページを辿ると全件がちょうど1回ずつ発行され_順は作成時刻昇順で_最後のページでカーソルが尽きる()
    {
        await using var factory = new TestWebApplicationFactory();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var seeded = new List<Document>();
        // 同じ作成時刻の文書を混ぜる（同時刻は ID 昇順で並ぶ —— 時刻だけのカーソルなら取りこぼす）。
        for (var i = 0; i < 7; i++)
            seeded.Add(await SeedAsync(factory, $"d{i}", Attrs("internal"), createdAt: t0.AddMinutes(i / 2)));
        var expectedOrder = seeded.OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).Select(d => d.Id).ToList();

        var admin = Admin(factory);
        string? cursor = null;
        var pages = 0;
        var remainingSeen = new List<int>();
        do
        {
            var r = await CallAsync(admin, new { dryRun = false, limit = 3, cursor });
            r.Matched.Should().Be(7);
            remainingSeen.Add(r.Remaining);
            r.Selected.Should().Be(Math.Min(3, r.Remaining));
            r.Published.Should().Be(r.Selected);
            cursor = r.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        pages.Should().Be(3, "7 件を 3 件ずつ —— 3 ページで尽きる");
        remainingSeen.Should().Equal(7, 4, 1);
        Published(factory).Select(e => e.DocumentId).Should().Equal(expectedOrder,
            "重複も抜けも無く、作成時刻昇順・同時刻は ID 昇順で 1 回ずつ");
    }

    [Fact]
    public async Task 走査の途中で作られた文書は末尾に現れ_createdBeforeを開始時刻に固定すれば選ばれない()
    {
        await using var factory = new TestWebApplicationFactory();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var a = await SeedAsync(factory, "a", Attrs("internal"), createdAt: t0);
        var b = await SeedAsync(factory, "b", Attrs("internal"), createdAt: t0.AddMinutes(1));
        var startedAt = t0.AddMinutes(2);
        var admin = Admin(factory);

        var first = await CallAsync(admin, new { dryRun = false, limit = 1, createdBefore = startedAt });
        // 1 ページ目の後に文書が作られる（作成時刻は走査の開始より後）。
        var late = await SeedAsync(factory, "late", Attrs("internal"), createdAt: t0.AddMinutes(5));

        var second = await CallAsync(admin, new { dryRun = false, limit = 1, cursor = first.NextCursor, createdBefore = startedAt });
        second.Matched.Should().Be(2);
        second.NextCursor.Should().BeNull();
        Published(factory).Select(e => e.DocumentId).Should().Equal([a.Id, b.Id],
            "開始時刻より後に作られた文書は、作成の経路で既に発行されているので選ばない");

        // 対照: 上限を与えずに最初から辿れば、途中で作られた文書は末尾に 1 回だけ現れる（読み飛ばしも重複も無い）。
        string? cursor = null;
        do
        {
            var r = await CallAsync(admin, new { dryRun = false, limit = 1, cursor });
            r.Matched.Should().Be(3);
            cursor = r.NextCursor;
        } while (cursor is not null && Published(factory).Count < 10);
        Published(factory).Select(e => e.DocumentId).Skip(2).Should().Equal([a.Id, b.Id, late.Id]);
    }

    // ── AC-3: 絞り込みと入力の検証 ─────────────────────────────────────────────

    [Fact]
    public async Task 絞り込みはidsと属性の完全一致のANDで効く()
    {
        await using var factory = new TestWebApplicationFactory();
        var hrInternal = await SeedAsync(factory, "hr-internal", Attrs("internal", ("department", "HR")));
        var hrLower = await SeedAsync(factory, "hr-lower", Attrs("internal", ("department", "hr")));
        var hrConfidential = await SeedAsync(factory, "hr-confidential", Attrs("confidential", ("department", "HR")));
        var admin = Admin(factory);

        var byAttr = await CallAsync(admin, new
        {
            dryRun = false,
            attributes = new Dictionary<string, string> { ["department"] = "HR", ["confidentiality"] = "internal" },
        });
        byAttr.Matched.Should().Be(1, "大文字小文字を区別する完全一致・AND");
        Published(factory).Select(e => e.DocumentId).Should().Equal([hrInternal.Id]);

        var byIds = await CallAsync(admin, new { dryRun = false, ids = new[] { hrLower.Id, hrConfidential.Id } });
        byIds.Matched.Should().Be(2);
        Published(factory).Select(e => e.DocumentId).Skip(1).Should().BeEquivalentTo([hrLower.Id, hrConfidential.Id]);
    }

    [Fact]
    public async Task 不正な要求は400で何も発行しない_dryRunの省略_空のキー_空の値_ids超過_不正なカーソル()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, "a", Attrs("internal"));
        var admin = Admin(factory);

        var cases = new (object Body, string Key)[]
        {
            (new { limit = 10 }, "dryRun"),
            (new { dryRun = false, attributes = new Dictionary<string, string> { [" "] = "x" } }, "attributes"),
            (new { dryRun = false, attributes = new Dictionary<string, string> { ["department"] = "" } }, "attributes.department"),
            (new { dryRun = false, ids = Enumerable.Range(0, RepublishSelection.MaxIds + 1).Select(_ => Guid.NewGuid()).ToArray() }, "ids"),
            (new { dryRun = false, cursor = "not-a-cursor" }, "cursor"),
        };
        foreach (var (body, key) in cases)
        {
            var resp = await admin.PostAsJsonAsync(Path, body, Ct);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, key);
            (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"{key}\"");
        }
        Published(factory).Should().BeEmpty();
    }

    // ── AC-4: dry-run ───────────────────────────────────────────────────────

    [Fact]
    public async Task dryRunは何も発行せず_残り全件の機密区分と本文なしと門で止まる件数を返す()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory, "pub", Attrs("public"));
        await SeedAsync(factory, "int", Attrs("internal"));
        await SeedAsync(factory, "int-nobody", Attrs("internal"), withBody: false);
        await SeedAsync(factory, "conf", Attrs("confidential"));
        await SeedAsync(factory, "unset", new Dictionary<string, string>());
        // 3 トグルとも OFF の個人資料（門で止まる）と、検索に含める個人資料（門を通る）。
        await SeedAsync(factory, "pn-off", PrivateNote(search: false));
        await SeedAsync(factory, "pn-on", PrivateNote(search: true));

        var r = await CallAsync(Admin(factory), new { dryRun = true, limit = 1 });

        r.DryRun.Should().BeTrue();
        r.Published.Should().Be(0);
        Published(factory).Should().BeEmpty("dry-run は発行しない");
        r.Matched.Should().Be(7);
        r.Remaining.Should().Be(7);
        r.Selected.Should().Be(7, "dry-run は limit に依らず残り全件を数える");
        r.NextCursor.Should().BeNull();
        r.WithoutBody.Should().Be(1);
        r.SkippedByGate.Should().Be(1);
        // 欠落は安全側（restricted）へ倒す。個人資料は restricted を持つ。0 件の区分も並ぶ。
        r.ByConfidentiality.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["public"] = 1,
            ["internal"] = 2,
            ["confidential"] = 1,
            ["restricted"] = 3,
        });
    }

    // ── AC-5: 中身は通常の経路と同じ ─────────────────────────────────────────────

    [Fact]
    public async Task 発行する中身は通常の経路と同じで_共有先とタグの表示名と本文指紋と本文の所在を載せる()
    {
        await using var factory = new TestWebApplicationFactory();
        Guid docId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            var tag = Tag.Create("規程");
            db.Tags.Add(tag);
            var id = Guid.NewGuid();
            var doc = Document.CreateWithBody(id, "就業規則", $"storage://documents/{id:N}/body.md", "https://example/x",
                "text/markdown", Attrs("internal", ("department", "HR")), [tag.Id], contentFingerprint: "fp-1");
            db.Documents.Add(doc);
            db.DocumentShares.Add(DocumentShare.Create(id, ShareSubjectType.User, "bob", grantedBy: "seed"));
            await db.SaveChangesAsync(Ct);
            docId = id;
        }
        var admin = Admin(factory);

        // 通常の経路（公開）が出すイベントを基準に取る。
        (await admin.PostAsync($"/documents/{docId}/publish", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var normal = Published(factory).Single();

        (await CallAsync(admin, new { dryRun = false, ids = new[] { docId } })).Published.Should().Be(1);
        var republished = Published(factory)[1];

        republished.Should().BeEquivalentTo(normal, "再発行は通常の経路と同じ関数で中身を載せる");
        republished.Tags.Should().Equal(["規程"], "識別子ではなく表示名");
        republished.SharedWith.Should().Equal(["bob"]);
        republished.ContentFingerprint.Should().Be("fp-1");
        republished.MarkdownUri.Should().NotBeNull();
    }

    [Fact]
    public async Task 再発行は台帳を書き換えない_版も更新時刻も動かない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedAsync(factory, "a", Attrs("internal"));

        await CallAsync(Admin(factory), new { dryRun = false });
        await CallAsync(Admin(factory), new { dryRun = false });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var stored = await db.Documents.Include(d => d.Versions).SingleAsync(d => d.Id == doc.Id, Ct);
        stored.UpdatedAt.Should().Be(doc.UpdatedAt);
        stored.Versions.Should().HaveCount(1);
        Published(factory).Should().HaveCount(2).And.OnlyContain(e => e.UpdatedAt == doc.UpdatedAt,
            "イベントは台帳の更新時刻を運ぶ（再索引のたびに更新時刻を今にしない）");
    }

    // ── AC-6: 発行の門 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 門で止まる個人資料は発行せずskippedByGateに数え_カーソルはその先へ進む()
    {
        await using var factory = new TestWebApplicationFactory();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(factory, "pn-off", PrivateNote(search: false), createdAt: t0);
        var on = await SeedAsync(factory, "pn-on", PrivateNote(search: true), createdAt: t0.AddMinutes(1));
        var org = await SeedAsync(factory, "org", Attrs("internal"), createdAt: t0.AddMinutes(2));
        var admin = Admin(factory);

        var first = await CallAsync(admin, new { dryRun = false, limit = 1 });
        first.Selected.Should().Be(1);
        first.Published.Should().Be(0);
        first.SkippedByGate.Should().Be(1);
        first.NextCursor.Should().NotBeNull("門で止めた文書の先へ進む（同じ文書で止まり続けない）");

        var rest = await CallAsync(admin, new { dryRun = false, limit = 5, cursor = first.NextCursor });
        rest.Published.Should().Be(2);
        rest.NextCursor.Should().BeNull();
        Published(factory).Select(e => e.DocumentId).Should().Equal([on.Id, org.Id]);
    }

    private static Dictionary<string, string> PrivateNote(bool search) => new()
    {
        [DocumentScopes.Key] = DocumentScopes.PrivateNote,
        ["owner"] = "alice",
        ["confidentiality"] = "restricted",
        [DocumentExposure.SearchKey] = search ? DocumentExposure.Included : DocumentExposure.Excluded,
        [DocumentExposure.GraphKey] = DocumentExposure.Excluded,
        [DocumentExposure.AiKey] = DocumentExposure.Excluded,
    };
}
