using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Tests.Features.Dashboard;

// NFR-02, FR-10, SC-10, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **受け口側の多層防御。** 1 枚目は BFF（発火の口）であり、通常の経路はそこで落ちる。
// ここが要るのは**受け口を直接叩かれた場合**であり、除外を「1 つの呼び出し元の作法」ではなく
// **行を作る側の性質**にするためである。
//
// 🔴 **検索傾向（ADR-0071）に独立した除外を置いていない**ことの根拠もここにある ——
// 合成の語が `UsageEvents` に一行も入らないなら、しきい値 3 を通過する余地が無い。
[Trait("TestKind", "Integration")]
public class SyntheticUsageEventExclusionTests
{
    private const string SyntheticSubject = "synthetic-monitor";

    private static TestWebApplicationFactory Factory() => new()
    {
        Settings = new Dictionary<string, string?>
        {
            // **空だと何も合成と見なさない**（fail-closed）ため、明示的に 1 件入れる。
            ["SyntheticMonitoring:Subjects:0"] = SyntheticSubject,
        }
    };

    // ★ 陽性: 合成の主体が直接投入しても行は作らない。202 は「受け取ったが記録しない」の意である。
    [Fact]
    public async Task PostEvents_WhenSyntheticPrincipal_DoesNotCreateRowAndDoesNotAppearInTrends()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, SyntheticSubject);

        // ADR-0071 のしきい値（3 件）を**超える**回数を投入する ——
        // しきい値未満で消えたのか除外で消えたのかを取り違えないため。
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync(
                "/dashboard/events", new UsageEventRequest("search", "合成監視の語"),
                TestContext.Current.CancellationToken);
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
                "合成は誤りではなく設計どおりの除外なので 400 にしない");
        }

        var trends = await factory.CreateClient().GetFromJsonAsync<List<SearchTrendDto>>(
            "/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();
        trends!.Should().NotContain(t => t.Term == "合成監視の語",
            "合成の語は ADR-0071 のしきい値を通過してはならない");
    }

    // ★ 陰性対照: 通常の主体が同じ回数だけ投入すれば、上位一覧に現れる。
    // 🔴 **これが無いと、上の陰性は「そもそも傾向が出ない器」でも緑になる。**
    [Fact]
    public async Task PostEvents_WhenOrdinaryPrincipal_CreatesRowAndAppearsInTrends()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync(
                "/dashboard/events", new UsageEventRequest("search", "実利用の語"),
                TestContext.Current.CancellationToken);
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var trends = await client.GetFromJsonAsync<List<SearchTrendDto>>(
            "/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();
        trends!.Should().Contain(t => t.Term == "実利用の語");
    }
}
