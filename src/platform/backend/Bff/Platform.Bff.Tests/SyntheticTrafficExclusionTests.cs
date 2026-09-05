using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// NFR-02, NFR-21, ADR-0044, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **合成監視のトラフィックを利用状況・検索傾向の集計から外す。**
//
// 計画 ADR-0076 決定 4 は「合成トラフィックは識別できる標識を持ち、ADR-0044 の LLM 費用計測と、
// FR-10 の利用状況・検索傾向（SC-10）の集計から除外する。**除外できない構成では合成監視を配備しない**」
// と定めた。**標識と除外は同時に入れる**という条件そのものを、ここで固定する。
//
// 🔴 **3 本を対で置く。** 陽性（合成は外れる）だけでは「常に外れている」でも緑になる。
//   1. 陽性: 合成の主体 → 利用イベントが発火しない
//   2. 陰性対照: 通常の主体 → 発火する（除外が過剰でないこと）
//   3. **偽装**: 外から `X-Synthetic-Traffic` を付けても発火する（**外周は受信ヘッダを見ない**）
//
// 単体か結合か: **結合**（BFF ホストを起こし、後段をスタブ化して経路全体を通す）。
[Trait("TestKind", "Integration")]
public class SyntheticTrafficExclusionTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);

    // 🔴 **「発火しない」は待ち時間で決まる**ので、陰性側の待ちは短くする。ただし
    // **陰性だけを見て「外れている」と結論しない** —— 同じクラスの陽性側（下の 2 本）が
    // 「同じ待ち方で確かに届く」ことを示す**陽性対照**である。
    private static readonly TimeSpan Absence = TimeSpan.FromSeconds(3);

    private HttpClient SyntheticClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, BffTestFactory.SyntheticSubject);
        return client;
    }

    // ★ 陽性: 標識つきの検索は `UsageEvents` に行を作らない。
    // **変異試験の対象**: `UsageEventReporter.Report` の合成除外（早期 return）を消すとここが落ちる。
    [Fact]
    public async Task PostSearch_WhenSyntheticPrincipal_DoesNotReportUsageEvent()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;

        var resp = await SyntheticClient().PostAsJsonAsync(
            "/bff/search", new { query = "合成監視の検索語", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "合成監視でも検索そのものは通る（経路の健全性を測るため）");
        (await factory.WaitForUsageEventAsync(Absence, TestContext.Current.CancellationToken))
            .Should().BeFalse("合成監視の検索は SC-10 の利用状況へ入れない（ADR-0076 決定 4）");
        factory.RecordedUsageEvents.Should().BeEmpty();
    }

    // ★ 陰性対照: 通常の主体は従来どおり発火する（除外が過剰に効いていないこと）。
    [Fact]
    public async Task PostSearch_WhenOrdinaryPrincipal_ReportsUsageEvent()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/search", new { query = "実利用の検索語", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue("合成でない検索は従来どおり数える");
        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.Query.Should().Be("実利用の検索語");
    }

    // 🔴 ★ **偽装**: 外から `X-Synthetic-Traffic` を付けても除外されない。
    //
    // これが通らないと、**利用者が自分の実トラフィックを費用・集計から隠せる。**
    // 外周（BFF）の判定材料は**検証済み JWT の主体だけ**であり、受信ヘッダではない。
    [Fact]
    public async Task PostSearch_WhenHeaderForgedFromOutside_StillReportsUsageEvent()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);

        var resp = await client.PostAsJsonAsync(
            "/bff/search", new { query = "偽装された検索語", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue("外から付けたヘッダで集計を免れてはならない（標識は主体で決まる）");
        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.Query.Should().Be("偽装された検索語");
    }

    // ★ 陽性: 回答（一括）も外れる。**3 経路すべてに効くこと**を回答側でも 1 本固定する。
    [Fact]
    public async Task PostAsk_WhenSyntheticPrincipal_DoesNotReportUsageEvent()
    {
        factory.ResetUsageEvents();

        var resp = await SyntheticClient().PostAsJsonAsync(
            "/bff/analysis/ask", new { question = "合成監視の質問" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Absence, TestContext.Current.CancellationToken)).Should().BeFalse();
        factory.RecordedUsageEvents.Should().BeEmpty();
    }

    // ★ 陽性: SSE 経路（SC-01 が実際に使う経路）も外れる。
    [Fact]
    public async Task PostAskStream_WhenSyntheticPrincipal_DoesNotReportUsageEvent()
    {
        factory.ResetUsageEvents();

        var req = new HttpRequestMessage(HttpMethod.Post, "/bff/analysis/ask/stream")
        {
            Content = JsonContent.Create(new { question = "合成監視のストリーム質問" })
        };
        req.Headers.Add(TestAuthHandler.ClientIdHeader, BffTestFactory.SyntheticSubject);
        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Absence, TestContext.Current.CancellationToken)).Should().BeFalse();
        factory.RecordedUsageEvents.Should().BeEmpty();
    }

    // ★ 内周への伝播（陽性）。**ここが切れると LlmGateway は費用から外せない** ——
    // 除外の可否がゲートウェイに届かず、決定 4 の「除外できない構成」に落ちる。
    [Fact]
    public async Task PostAsk_WhenSyntheticPrincipal_PropagatesMarkerDownstream()
    {
        factory.ResetUsageEvents();
        factory.LastForwardedSyntheticHeader = null;

        var resp = await SyntheticClient().PostAsJsonAsync(
            "/bff/analysis/ask", new { question = "合成監視の質問" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastForwardedSyntheticHeader.Should().Be(SyntheticTraffic.HeaderValue);
    }

    // ★ 内周への伝播（陰性対照）。通常の主体では**付けない** ——
    // 常に付けていたら、上の陽性は「常に真」でも緑になる。
    [Fact]
    public async Task PostAsk_WhenOrdinaryPrincipal_DoesNotPropagateMarker()
    {
        factory.ResetUsageEvents();
        factory.LastForwardedSyntheticHeader = null;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/analysis/ask", new { question = "実利用の質問" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastForwardedSyntheticHeader.Should().BeNull();

        // 🔴 **後始末**: 本テストは（合成でないため）利用イベントを 1 件発火する。
        // 待たずに抜けると、**次のテストが `ResetUsageEvents` した直後に遅れて届き**、
        // 「1 件のはず」が 2 件になって落ちる（実測で発生した）。ここで排出しきる。
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    // ★ 偽装（内周への伝播）。外から付けたヘッダを**転送しない**。
    // 転送してしまうと、外周で主体判定を厳しくしても**内周で費用が外れる**。
    [Fact]
    public async Task PostAsk_WhenHeaderForgedFromOutside_DoesNotPropagateMarker()
    {
        factory.ResetUsageEvents();
        factory.LastForwardedSyntheticHeader = null;

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);
        var resp = await client.PostAsJsonAsync(
            "/bff/analysis/ask", new { question = "偽装された質問" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.LastForwardedSyntheticHeader.Should().BeNull("外周は受信ヘッダを転送しない");

        // 🔴 **後始末**: 本テストは（合成でないため）利用イベントを 1 件発火する。
        // 待たずに抜けると、**次のテストが `ResetUsageEvents` した直後に遅れて届き**、
        // 「1 件のはず」が 2 件になって落ちる（実測で発生した）。ここで排出しきる。
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }
}
