using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// NFR-02, ADR-0079 決定 1・2, ADR-0076 決定 4, [[IADR-0654]] (#2335):
// **合成の 2 段を要求ごとに区別して内周へ運ぶ。** SLO 評価用（60 分・LLM を呼ぶ）の主体の要求だけ
// `X-Synthetic-Traffic: slo` を付け、常時（60 秒・LLM を呼ばない）の主体は従前の `1` を付ける。
// 🔴 **外周は受信ヘッダを見ない**（`slo` を外から付けても運ばない）。外から付けられると、利用者が LLM の費用を
// 合成として計上から外せてしまう。SLO の要求も合成として利用状況から外れる（ADR-0076 決定 4）。
[Trait("TestKind", "Integration")]
public class SyntheticSloProbeBffTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Absence = TimeSpan.FromSeconds(3);

    private HttpClient ClientAs(string? clientId)
    {
        var client = factory.CreateClient();
        if (clientId is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, clientId);
        return client;
    }

    // ★ 経路 3 本（一括・分析・SSE）すべてで、主体ごとの値を運ぶ。
    [Theory]
    [InlineData("/bff/analysis/ask", BffTestFactory.SloSyntheticSubject, SyntheticTraffic.SloHeaderValue)]
    [InlineData("/bff/analysis/ask/stream", BffTestFactory.SloSyntheticSubject, SyntheticTraffic.SloHeaderValue)]
    [InlineData("/bff/analysis/analyze", BffTestFactory.SloSyntheticSubject, SyntheticTraffic.SloHeaderValue)]
    [InlineData("/bff/analysis/ask", BffTestFactory.SyntheticSubject, SyntheticTraffic.HeaderValue)]
    [InlineData("/bff/analysis/ask/stream", BffTestFactory.SyntheticSubject, SyntheticTraffic.HeaderValue)]
    [InlineData("/bff/analysis/analyze", BffTestFactory.SyntheticSubject, SyntheticTraffic.HeaderValue)]
    public async Task 合成の主体ごとに内周へ運ぶ値が分かれ利用状況には数えない(string path, string clientId, string expected)
    {
        factory.ResetUsageEvents();
        factory.LastForwardedSyntheticHeader = null;

        var resp = await ClientAs(clientId).PostAsJsonAsync(path, Body(path), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastForwardedSyntheticHeader.Should().Be(expected);
        (await factory.WaitForUsageEventAsync(Absence, TestContext.Current.CancellationToken))
            .Should().BeFalse("SLO の要求も合成であり、SC-10 の利用状況へ入れない（ADR-0076 決定 4）");
    }

    // 🔴 ★ 偽装: 外から `slo` を付けても運ばない（通常の主体として扱い、利用状況にも数える）。
    [Fact]
    public async Task 外から付けたsloは運ばない()
    {
        factory.ResetUsageEvents();
        factory.LastForwardedSyntheticHeader = null;
        var client = ClientAs(null);
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.SloHeaderValue);

        var resp = await client.PostAsJsonAsync("/bff/analysis/ask", Body("/bff/analysis/ask"),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastForwardedSyntheticHeader.Should().BeNull("外周は受信ヘッダを転送しない");
        // 後始末（陽性対照を兼ねる）: 通常の要求は利用イベントを 1 件発火する。次のテストへ遅れて届かないよう排出する。
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    private static object Body(string path) => path.EndsWith("/analyze", StringComparison.Ordinal)
        ? new { instruction = "合成の分析" }
        : new { question = "合成の質問" };
}
