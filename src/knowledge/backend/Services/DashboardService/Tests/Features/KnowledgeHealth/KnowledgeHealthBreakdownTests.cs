using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using DashboardService.Domain;
using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Features.KnowledgeHealth.View;

namespace DashboardService.Tests.Features.KnowledgeHealth;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006, ADR-0033 決定 9,
// [[IADR-0265]], [[IADR-0389]] 決定 1 (#1246): 観測値の**内訳の軸**。
//
// IADR-0265 が先送りしていた「指標 1 つ＝件数 1 つ」を解く。辺の型ごとの使用件数
// （`edge-type-usage`）は内訳が無ければ**合計しか出せず、どの型が使われているか分からない**。
//
// 🔴 **既存の 3 つの統制（件数のみ・ロール限定・個人資料除外）を内訳が迂回しないこと**が
// 本クラスの主眼である。内訳は「集計の粒度を細かくする」変更であり、
// **粒度を細かくすると除外が漏れやすい**（`KnowledgeHealthEndpointTests` の否定形は
// 合計だけを見ており、内訳の側は見ていない）。
[Trait("TestKind", "Integration")]
public class KnowledgeHealthBreakdownTests
{
    // 🔴 送信側 HttpKnowledgeHealthReporter.ObservationsPath の値（リテラルで持つ理由は
    // `KnowledgeHealthEndpointTests` の同じ定数のコメント）。
    private const string ProducerObservationsPath = "/internal/knowledge-health/observations";

    private static KnowledgeHealthIndicatorDto IndicatorOf(KnowledgeHealthDto health, string name)
        => health.Indicators.Single(i => i.Indicator == name);

    // ── 内訳が返る ────────────────────────────────────────────────

    [Fact]
    public async Task 軸を添えた観測値は軸ごとの内訳として返る()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(KnowledgeHealthIndicators.EdgeTypeUsage,
            [
                new KnowledgeHealthObservationRequest("edge-1", null, "related"),
                new KnowledgeHealthObservationRequest("edge-2", null, "related"),
                new KnowledgeHealthObservationRequest("edge-3", null, "supersedes"),
            ]),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var usage = IndicatorOf(health!, KnowledgeHealthIndicators.EdgeTypeUsage);
        usage.Count.Should().Be(3);
        usage.Breakdown.Should().NotBeNull();
        usage.Breakdown!.Select(b => (b.Dimension, b.Count)).Should().Equal(
            [("related", 2), ("supersedes", 1)],
            "件数の降順・同数は軸名の昇順（表示のたびに順序が変わらないこと）");
    }

    // 🔴 **内訳の合計は件数と一致する。** ずれると、除外された分が内訳の差分として漏れる。
    [Fact]
    public async Task 個人資料は内訳からも除外され合計と一致する()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(KnowledgeHealthIndicators.EdgeTypeUsage,
            [
                new KnowledgeHealthObservationRequest("edge-1", null, "related"),
                new KnowledgeHealthObservationRequest("edge-2", "private-note", "related"),
                new KnowledgeHealthObservationRequest("edge-3", "private-note", "cites"),
            ]),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var usage = IndicatorOf(health!, KnowledgeHealthIndicators.EdgeTypeUsage);
        usage.Count.Should().Be(1, "陽性対照: 除外は従来どおり効いている");
        usage.Breakdown!.Sum(b => b.Count).Should().Be(usage.Count,
            "内訳の合計が件数を超えると、個人資料の件数が内訳の差分として漏れる");
        usage.Breakdown.Should().ContainSingle().Which.Dimension.Should().Be("related");
        // 🔴 陰性: **個人資料しか無かった軸は内訳に現れない**（軸名だけでも存在が伝わる）。
        usage.Breakdown.Should().NotContain(b => b.Dimension == "cites");
    }

    // ── 内訳を持たない指標 ────────────────────────────────────────

    // 🔴 **null と空リストは意味が違う。** 軸を持たない指標に空の内訳を返すと、
    // 画面は「内訳はあるが 0 件」と読む。孤立文書数は従来どおり軸を持たない。
    [Fact]
    public async Task 軸を添えない指標の内訳はnullである()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(KnowledgeHealthIndicators.OrphanDocuments,
            [
                new KnowledgeHealthObservationRequest("doc-1"),
                new KnowledgeHealthObservationRequest("doc-2"),
            ]),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var orphans = IndicatorOf(health!, KnowledgeHealthIndicators.OrphanDocuments);
        orphans.Count.Should().Be(2, "陽性対照: 件数は従来どおり数えられている");
        orphans.Breakdown.Should().BeNull();
    }

    // 観測値が 1 件も無い指標も内訳は null（0 埋めは件数だけ。7 指標は欠落させない）。
    [Fact]
    public async Task 観測値の無い指標は件数0で内訳はnullである()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Should().HaveCount(7, "0 件の指標も欠落させない");
        var unresolved = IndicatorOf(health, KnowledgeHealthIndicators.UnresolvedLinks);
        unresolved.Count.Should().Be(0);
        unresolved.Breakdown.Should().BeNull();
    }

    // ── 生産者が組み立てる生の JSON ────────────────────────────────

    // 🔴 **綴りと大小は型では守られない**（送信側は匿名オブジェクトを組み立てる）。
    // `HttpKnowledgeHealthReporter` が実際に投げる形をそのまま束縛できることを固定する。
    [Fact]
    public async Task 生産者が組み立てる生のJSONで軸を束縛できる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        const string body = """
            {"indicator":"unresolved-links","observations":[
              {"subjectKey":"aaaa:0011","docScope":null,"dimension":"not-found"},
              {"subjectKey":"aaaa:0022","docScope":null,"dimension":"ambiguous"},
              {"subjectKey":"bbbb:0033","docScope":null,"dimension":"not-found"}]}
            """;

        var resp = await client.PostAsync(ProducerObservationsPath,
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        IndicatorOf(health!, KnowledgeHealthIndicators.UnresolvedLinks).Breakdown!
            .Select(b => (b.Dimension, b.Count)).Should().Equal([("not-found", 2), ("ambiguous", 1)]);
    }

    // 軸を持つ観測値と持たない観測値が同じ指標に混ざっても、**合計は全件**で内訳は軸のある分だけ。
    // （生産者の実装ミスで軸が抜けたとき、件数が静かに減らないこと。）
    [Fact]
    public async Task 軸の欠けた観測値も件数には数える()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(KnowledgeHealthIndicators.UnresolvedLinks,
            [
                new KnowledgeHealthObservationRequest("k-1", null, "not-found"),
                new KnowledgeHealthObservationRequest("k-2"),
            ]),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var unresolved = IndicatorOf(health!, KnowledgeHealthIndicators.UnresolvedLinks);
        unresolved.Count.Should().Be(2, "軸が無いことを理由に件数から落とさない");
        unresolved.Breakdown!.Sum(b => b.Count).Should().Be(1);
    }

    // 空白だけの軸は「軸なし」に倒す（名前の無い内訳行を作らない）。
    [Fact]
    public async Task 空白だけの軸は軸なしとして扱う()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(KnowledgeHealthIndicators.UnresolvedLinks,
                [new KnowledgeHealthObservationRequest("k-1", null, "   ")]),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var unresolved = IndicatorOf(health!, KnowledgeHealthIndicators.UnresolvedLinks);
        unresolved.Count.Should().Be(1);
        unresolved.Breakdown.Should().BeNull();
    }
}
