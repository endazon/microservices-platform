using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DashboardService.Features.KnowledgeHealth;
using DashboardService.Domain;

namespace DashboardService.Tests;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006 (#443): ナレッジ健全性指標。
// 計画 §ナレッジ健全性の指標 は **ABAC の文書単位判定に対する明示的な例外**として全体集計を許すが、
// **件数のみ・ロール限定・個人資料除外の 3 つを同時に満たすことが条件**である。
// 3 つのうち 1 つでも欠けると存在秘匿が崩れるため、**それぞれを独立にテストで固定する**。
public class KnowledgeHealthEndpointTests
{
    private static KnowledgeHealthReportRequest Report(
        string indicator, params KnowledgeHealthObservationRequest[] observations)
        => new(indicator, observations);

    // FR-10, FR-17 (T-20): 観測値を報告すると、指標ごとの件数として集計される。
    [Fact]
    public async Task 報告した観測値が指標ごとの件数として集計される()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1", "organization"),
                new KnowledgeHealthObservationRequest("doc-2", "organization")),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Should().ContainSingle(i =>
            i.Indicator == KnowledgeHealthIndicators.OrphanDocuments && i.Count == 2);
    }

    // FR-10, FR-19, SC-10 (T-21): 🔴 **個人資料（private-note）は集計から除外される。**
    // 除外は所有者本人が閲覧する場合も含め**一律**である（例外を設けると集計値がロールごとに変わり、
    // 「集計範囲は全体」という前提が崩れる）。件数の変動から個人資料の存在が推測される経路も塞ぐ。
    [Fact]
    public async Task 個人資料は集計から除外される()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-org", "organization"),
                new KnowledgeHealthObservationRequest("doc-private", KnowledgeDocScopes.PrivateNote),
                new KnowledgeHealthObservationRequest("doc-private-upper", "PRIVATE-NOTE")),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.OrphanDocuments)
            .Count.Should().Be(1, "個人資料は綴りの大小に関わらず一律で除外される");
    }

    // FR-10, FR-19 (T-22): 🔴 **除外は集合帰属で判定する。**「organization でない」で書いてはならない。
    // `doc_scope` を持たない文書（実データの大半）が個人資料と見なされると、**指標が一斉に 0 になる**。
    // この陽性対照が無いと、2 つの実装は動作で見分けがつかない。
    [Fact]
    public async Task スコープ属性を持たない観測値は集計に含まれる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.UnresolvedLinks,
                new KnowledgeHealthObservationRequest("edge-1"),
                new KnowledgeHealthObservationRequest("edge-2", null)),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.UnresolvedLinks)
            .Count.Should().Be(2);
    }

    // FR-10, SC-10 (T-23): 運用者は閲覧できる（計画: 閲覧ロールは運用者・システム管理者）。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("platform-admin")]
    public async Task 運用者と管理者は閲覧できる(string role)
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/knowledge-health");
        req.Headers.Add(TestAuthHandler.RolesHeader, role);

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-10, SC-10 (T-24): 🔴 **運用者・管理者以外は 403 であり、件数を 1 つも返さない。**
    // 全体集計を許す以上、**閲覧側のロール制限が唯一の統制点**である（計画）。
    // 否定形（本文に指標名も件数も現れない）まで見るのは、403 に部分結果を載せる実装を止めるためである。
    [Fact]
    public async Task 運用者以外は403で件数を一切返さない()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1", "organization")),
            TestContext.Current.CancellationToken);

        var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/knowledge-health");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(KnowledgeHealthIndicators.OrphanDocuments);
        body.Should().NotContain("count");
    }

    // FR-10, SC-10 (T-25): 🔴 **応答は件数のみで、文書の識別子を含まない。**
    // ドリルダウンの導線を設けないのは、閲覧ロールを限定していても文書名を出すと
    // ABAC の文書単位判定を迂回して個々の文書の存在が伝わるためである。
    [Fact]
    public async Task 応答に文書の識別子は含まれない()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.StaleDocuments,
                new KnowledgeHealthObservationRequest("経費規程-2024", "organization")),
            TestContext.Current.CancellationToken);

        var body = await client.GetStringAsync(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        body.Should().NotContain("経費規程-2024");
        body.Should().NotContain("subjectKey");
    }

    // FR-10, SC-10 (T-26): 7 指標すべてを 0 埋めして返す（欠落と 0 を混同させない）。
    [Fact]
    public async Task 観測値が無い指標も0件として返る()
    {
        using var factory = new TestWebApplicationFactory();

        var health = await factory.CreateClient().GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Should().HaveCount(KnowledgeHealthIndicators.All.Count);
        health.Indicators.Should().OnlyContain(i => i.Count == 0);
        health.ObservedAt.Should().BeNull();
    }

    // FR-10 (T-27): 報告はスナップショット置換である（差分ではない）。
    // 解消した観測値を取り消す経路を別に持つと、取り消し漏れが件数を恒久的に膨らませる。
    [Fact]
    public async Task 報告は指標単位のスナップショット置換である()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1"),
                new KnowledgeHealthObservationRequest("doc-2")),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/knowledge-health/observations",
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1")),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.OrphanDocuments)
            .Count.Should().Be(1);
    }

    // FR-10 (T-28): 指標の語彙は閉じる。未知の指標名は 400。
    // 語彙が開いていると、生産者側の綴り違いが「0 件の指標」として静かに現れ、改善したと読める。
    [Fact]
    public async Task 未知の指標名は400()
    {
        using var factory = new TestWebApplicationFactory();

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/dashboard/knowledge-health/observations",
            Report("orphan-docs", new KnowledgeHealthObservationRequest("doc-1")),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
