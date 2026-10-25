using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.AstStaleCopies;
using DocumentService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.AstStaleCopies;

// FR-06, FR-05, SC-05, NFR-09, 計画 ADR-0122 決定 1・2・3・4, ADR-0121 決定 3, [[IADR-0484]] (#1667):
// **AST の古い写しを列挙する口（`GET /documents/ast-stale-copies`）。管理者だけ・読み取り専用。**
//
// 🔴 **陰性は陽性の対照と対で置く。** 同じ台帳に AST の写し（対象）と、同じ形の AST 以外の文書（取り込みの経路の
// `owner=system`・別の project・個人資料・人が作った文書）を並べ、対象だけが返り、残りが理由ごとに 1 件ずつ数えられることを示す。
//
// 件数を正確に数えるため、**試験ごとに器（＝ InMemory の DB）を作る**（クラスで器を共有すると他の試験の文書が混ざる）。
//
// 🔴 **変異試験の対象である**（作業仕様書 AC-7）: owner の条件を `system` だけにする・作成の経路の条件を外す・project の条件を外す・
//   個人資料の除外を外す、のいずれでも `同じ形の文書のうちAST_の古い写しだけが対象になり…` が落ちる。
[Trait("TestKind", "Integration")]
public class AstStaleCopiesEndpointTests
{
    private const string Path = "/documents/ast-stale-copies";
    private const string KbWriter = "service-account-ai-stock-trading-kb-writer";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient ClientAs(TestWebApplicationFactory factory, string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static HttpClient Admin(TestWebApplicationFactory factory) => ClientAs(factory, "admin-1", "platform-admin");

    private static Dictionary<string, string> Report(string periodKey, string? owner = null, string? project = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["kind"] = "Daily",
            ["periodKey"] = periodKey,
            ["assumptionsVersion"] = "3",
        };
        if (owner is not null) attributes["owner"] = owner;
        if (project is not null) attributes["project"] = project;
        return attributes;
    }

    private static Dictionary<string, string> Article(string publishedAt, string? owner = null, string? project = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["kind"] = "News",
            ["source"] = "tdnet",
            ["publishedAt"] = publishedAt,
        };
        if (owner is not null) attributes["owner"] = owner;
        if (project is not null) attributes["project"] = project;
        return attributes;
    }

    private static string ReportTitle(string periodKey) => $"確定報告書 Daily {periodKey}";

    private static async Task<Document> SeedAsync(TestWebApplicationFactory factory, Func<Document> create)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = create();
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    // `POST /documents` の経路（最初の版が `created` / `created-with-body`）を台帳へ直接作る（所有者を任意に置くため）。
    private static Task<Document> SeedCreatedAsync(TestWebApplicationFactory factory, string title,
        Dictionary<string, string> attributes, bool withBody = false)
        => SeedAsync(factory, () => withBody
            ? Document.CreateWithBody(Guid.NewGuid(), title, "storage://documents/x/body.md", null, "text/markdown", attributes)
            : Document.Create(title, originalUri: null, contentType: null, attributes: attributes));

    // 取り込みの経路（最初の版が `normalized`）。
    private static Task<Document> SeedNormalizedAsync(TestWebApplicationFactory factory, string title,
        Dictionary<string, string> attributes)
        => SeedAsync(factory, () => Document.CreateNormalized(Guid.NewGuid(), title, "storage://documents/y/body.md", attributes));

    private static async Task<AstStaleCopiesResponse> ListAsync(HttpClient client)
    {
        var resp = await client.GetAsync(Path, Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<AstStaleCopiesResponse>(Ct))!;
    }

    // ── AC-2・AC-3・AC-4: 同じ形の文書のうち、AST の古い写しだけが対象になる ─────────────

    [Fact]
    public async Task 同じ形の文書のうちAST_の古い写しだけが対象になり_除いた文書は理由ごとに1件ずつ数えられる()
    {
        await using var factory = new TestWebApplicationFactory();

        // 陽性（対象）: 報告書 2・記事 2。owner の欠落と system、project のあり・なし、本文のあり・なしを混ぜる。
        var p1 = await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01"));
        var p2 = await SeedCreatedAsync(factory, "表題を変えた報告書", Report("2026-07-02", "system", "ai-stock-trading"), withBody: true);
        var p3 = await SeedCreatedAsync(factory, "記事 A", Article("2026-06-15T00:00:00.0000000+00:00"), withBody: true);
        var p4 = await SeedCreatedAsync(factory, "記事 B", Article("2026-08-20T12:00:00.0000000+09:00", "system", "ai-stock-trading"));

        // 陰性（陽性から 1 つだけ変える）。
        await SeedNormalizedAsync(factory, ReportTitle("2026-07-02"), Report("2026-07-02", "system", "ai-stock-trading")); // 取り込みの経路
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", project: "other-project"));      // 別の project
        var privateNote = Article("2026-06-15T00:00:00.0000000+00:00");
        privateNote["doc_scope"] = "private-note";
        await SeedCreatedAsync(factory, "記事 A", privateNote, withBody: true);                                            // 個人資料
        await SeedCreatedAsync(factory, "表題を変えた報告書", Report("2026-07-01"));                                       // project なしで表題が違う
        await SeedCreatedAsync(factory, "社内規程", new() { ["confidentiality"] = "internal", ["owner"] = "system" });     // 人の文書（system）
        await SeedCreatedAsync(factory, "議事録", new() { ["confidentiality"] = "internal" });                             // 人の文書（owner なし）
        await SeedCreatedAsync(factory, ReportTitle("2026-07-03"), Report("2026-07-03", KbWriter, "ai-stock-trading"));   // 現在のアカウント
        await SeedCreatedAsync(factory, "記事 C", Article("2026-06-15T00:00:00.0000000+00:00", "alice"));                 // 他の主体

        var result = await ListAsync(Admin(factory));

        result.Items.Select(i => i.Id).Should().BeEquivalentTo([p1.Id, p2.Id, p3.Id, p4.Id]);
        result.Scanned.Should().Be(12);
        result.Targets.Total.Should().Be(4);
        result.Targets.Reports.Count.Should().Be(2);
        result.Targets.Articles.Count.Should().Be(2);
        result.Excluded.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["private-note"] = 1,
            ["not-created-via-post"] = 1,
            ["other-project"] = 1,
            ["not-ast-shape"] = 3,
            ["owned-by-current-account"] = 1,
            ["other-owner"] = 1,
        });
        (result.Targets.Total + result.Excluded.Values.Sum()).Should().Be(result.Scanned);

        // 決定 2: 記事の期間（publishedAt）を示す。
        result.Targets.Articles.PublishedFrom.Should().Be(DateTimeOffset.Parse("2026-06-15T00:00:00+00:00"));
        result.Targets.Articles.PublishedTo.Should().Be(DateTimeOffset.Parse("2026-08-20T03:00:00+00:00"));
        result.Targets.Reports.CreatedFrom.Should().NotBeNull();
        result.Targets.Reports.CreatedTo.Should().BeOnOrAfter(result.Targets.Reports.CreatedFrom!.Value);

        var item1 = result.Items.Single(i => i.Id == p1.Id);
        item1.Should().BeEquivalentTo(new
        {
            Category = "report",
            Owner = "missing",
            HasProject = false,
            Kind = "Daily",
            PeriodKey = "2026-07-01",
            PublishedAt = (DateTimeOffset?)null,
        });
        var item4 = result.Items.Single(i => i.Id == p4.Id);
        item4.Should().BeEquivalentTo(new { Category = "article", Owner = "system", HasProject = true, PeriodKey = (string?)null });
    }

    [Fact]
    public async Task 作成の口で主体の名前が無く作られた写しは対象で_現在のサービスアカウントが作った写しは対象でない()
    {
        await using var factory = new TestWebApplicationFactory();

        // 主体の名前が取れない呼び出し元（機械とも判定されない）＝ owner が載らない（#1057 から #1616 までの AST の写しの形）。
        var nameless = ClientAs(factory, "ignored", "platform-operator");
        nameless.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        var stale = await nameless.PostAsJsonAsync("/documents", new
        {
            title = ReportTitle("2026-09-01"),
            attributes = Report("2026-09-01", owner: "system", project: "ai-stock-trading"),
            body = "# 報告",
        }, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Created);
        var staleDoc = (await stale.Content.ReadFromJsonAsync<Knowledge.Contracts.Dtos.DocumentDto>(Ct))!;

        // 陽性の対照: 現在のサービスアカウント（#1616 以降の AST）が作った写し。
        var current = await ClientAs(factory, KbWriter, "platform-operator").PostAsJsonAsync("/documents", new
        {
            title = ReportTitle("2026-09-02"),
            attributes = Report("2026-09-02", project: "ai-stock-trading"),
        }, Ct);
        current.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await ListAsync(Admin(factory));

        result.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { staleDoc.Id, Owner = "missing" });
        result.Excluded["owned-by-current-account"].Should().Be(1);
        result.CurrentAccountReports.Count.Should().Be(1);
    }

    // ── AC-1: 管理者だけ ─────────────────────────────────────────────────────

    [Fact]
    public async Task 管理者だけが引け_運用者の人とAST_の書き手と一般の利用者は403()
    {
        await using var factory = new TestWebApplicationFactory();

        (await Admin(factory).GetAsync(Path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientAs(factory, "operator-1", "platform-operator").GetAsync(Path, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, KbWriter, "platform-operator").GetAsync(Path, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ClientAs(factory, "user-1", "platform-user").GetAsync(Path, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 口は認証を要しAdminOnlyを積む()
    {
        await using var factory = new TestWebApplicationFactory();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/documents" + ListAstStaleCopiesEndpoint.Route);

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Should().Contain(a => a.Policy == PlatformAuthPolicies.AdminOnly);
    }

    // ── AC-5: 書き込まない ─────────────────────────────────────────────────

    [Fact]
    public async Task 列挙は台帳を書き換えずイベントも出さない()
    {
        await using var factory = new TestWebApplicationFactory();
        var doc = await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01"));
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var publishedBefore = bus.PublishedOf<object>().Count;

        (await ListAsync(Admin(factory))).Targets.Total.Should().Be(1);
        (await ListAsync(Admin(factory))).Targets.Total.Should().Be(1, "2 回引いても同じ（消さない）");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var stored = await db.Documents.Include(d => d.Versions).SingleAsync(d => d.Id == doc.Id, Ct);
        stored.Version.Should().Be(1);
        stored.Versions.Should().ContainSingle();
        stored.Attributes.Should().NotContainKey("owner", "owner を遡及して付けない（ADR-0122 決定 1）");
        (await db.Documents.CountAsync(Ct)).Should().Be(1);
        bus.PublishedOf<object>().Count.Should().Be(publishedBefore);
    }

    // ── AC-6・ADR-0122 決定 3: 入れ直しの後と切替の後の確認 ──────────────────────

    [Fact]
    public async Task 現在のサービスアカウントの報告書の写しが重複していればその組を返す()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", KbWriter, "ai-stock-trading"));
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", KbWriter, "ai-stock-trading"), withBody: true);
        await SeedCreatedAsync(factory, ReportTitle("2026-07-02"), Report("2026-07-02", KbWriter, "ai-stock-trading"));

        var result = await ListAsync(Admin(factory));

        result.Targets.Total.Should().Be(0);
        result.CurrentAccountReports.Count.Should().Be(3);
        result.CurrentAccountReports.Duplicates.Should().ContainSingle()
            .Which.Should().Be(new AstDuplicatedReport("Daily", "2026-07-01", 2));
    }

    // ── FR-19, #1891: 承認待ちの写し（`reportState=draft`）は重複の組に出ない ─────────────────
    //
    // 陽性: 確定の後に削除が失敗して残ったドラフト（確定版より古い）と確定版は組にならない（runbook の「新しい方を消す」で確定版を消さない）。
    // 陰性: 確定版どうしの重複は従来どおり組として出て、ドラフトを足しても組の件数は変わらない。

    private static Dictionary<string, string> Draft(string periodKey)
    {
        var attributes = Report(periodKey, KbWriter, "ai-stock-trading");
        attributes["reportState"] = "draft";
        foreach (var key in new[] { "search_exposure", "graph_exposure", "ai_input" }) attributes[key] = "excluded";
        return attributes;
    }

    [Fact]
    public async Task 残った承認待ちの写しと確定版は重複の組にならず_古い写しの対象にもならない()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedCreatedAsync(factory, "報告書ドラフト Daily 2026-07-01", Draft("2026-07-01"), withBody: true);   // 古い（残ったドラフト）
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", KbWriter, "ai-stock-trading"), withBody: true);

        var result = await ListAsync(Admin(factory));

        result.Targets.Total.Should().Be(0);
        result.CurrentAccountReports.Count.Should().Be(1);
        result.CurrentAccountReports.Duplicates.Should().BeEmpty();
        result.Excluded["not-ast-shape"].Should().Be(1);
        result.Excluded["owned-by-current-account"].Should().Be(1);
    }

    [Fact]
    public async Task 確定版どうしの重複はドラフトが同じ台帳にあっても従来どおり組として返る()
    {
        await using var factory = new TestWebApplicationFactory();
        await SeedCreatedAsync(factory, "報告書ドラフト Daily 2026-07-01", Draft("2026-07-01"), withBody: true);
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", KbWriter, "ai-stock-trading"));
        await SeedCreatedAsync(factory, ReportTitle("2026-07-01"), Report("2026-07-01", KbWriter, "ai-stock-trading"), withBody: true);

        var result = await ListAsync(Admin(factory));

        result.CurrentAccountReports.Count.Should().Be(2);
        result.CurrentAccountReports.Duplicates.Should().ContainSingle()
            .Which.Should().Be(new AstDuplicatedReport("Daily", "2026-07-01", 2));
    }

    [Fact]
    public async Task 台帳が空なら対象は0件で_理由は0件でも並び_期間はnull()
    {
        await using var factory = new TestWebApplicationFactory();

        var result = await ListAsync(Admin(factory));

        result.Scanned.Should().Be(0);
        result.Targets.Total.Should().Be(0);
        result.Items.Should().BeEmpty();
        result.Excluded.Keys.Should().BeEquivalentTo(AstStaleCopyRules.Reasons.All);
        result.Excluded.Values.Should().AllSatisfy(v => v.Should().Be(0));
        result.Targets.Reports.CreatedFrom.Should().BeNull();
        result.Targets.Articles.PublishedFrom.Should().BeNull();
        result.CurrentAccountReports.Duplicates.Should().BeEmpty();
    }
}
