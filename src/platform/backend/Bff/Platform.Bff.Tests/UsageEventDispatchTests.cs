using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-10, SC-10, ADR-0006, IADR-0336 (#1103): 利用状況イベント（`POST /dashboard/events`）の**発火側**。
//
// 🔴 受け口・永続化・集計・画面はすべて在ったが、**投入する製品コードが 1 本も無かった**ため
// SC-10 の利用状況・検索傾向は恒久的に 0 だった。**「0 件」と「一度も測っていない」は
// 画面上で区別できない**ので、発火はここで固定する。
//
// 送出は要求の応答経路から外れている（有界の列 ＋ 常駐ドレイン）ため、各テストは
// `WaitForUsageEventAsync` で**届くのを待つ**。待たずに数えると「まだ届いていない」を
// 「発火していない」と読み違える。
//
// 単体か結合か: **結合**（BFF ホストを起こし、後段をスタブ化して経路全体を通す）。
public class UsageEventDispatchTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);

    // ★ 発火（受け入れ基準「検索を 1 回実行すると `search` の行が 1 件増える」の BFF 層）。
    // **変異試験の対象**: `SearchBffEndpoints` の `usage.Report(...)` を消すとこのテストが落ちる。
    [Fact]
    public async Task PostSearch_WhenSucceeded_ReportsSearchUsageEvent()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/search", new { query = "経費精算", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue("検索の成功は利用状況イベントを 1 件発火する");

        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { EventType = UsageEventType.Search, Query = "経費精算" },
                o => o.ExcludingMissingMembers());
    }

    // ★ 発火（回答）＋ **最小フィールド**。受け口は種別が `answer` のとき検索語を捨てるので、
    // 質問文（利用者の自由文）を経路と相手側のログに晒さない（IADR-0336 決定 5）。
    [Fact]
    public async Task PostAsk_WhenSucceeded_ReportsAnswerUsageEventWithoutQuestionText()
    {
        factory.ResetUsageEvents();

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/analysis/ask", new { question = "退職金の計算式は？" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();

        var recorded = factory.RecordedUsageEvents.Should().ContainSingle().Subject;
        recorded.EventType.Should().Be(UsageEventType.Answer);
        recorded.Query.Should().BeNull("受け口が捨てる質問文を送らない（自由文を経路へ出さない）");
    }

    // ★ 発火（SSE の回答）。**SPA が既定で使う経路**であり、落とすと回答がほぼ数えられない。
    [Fact]
    public async Task PostAskStream_WhenUpstreamAccepts_ReportsAnswerUsageEvent()
    {
        factory.ResetUsageEvents();

        var req = new HttpRequestMessage(HttpMethod.Post, "/bff/analysis/ask/stream")
        {
            Content = JsonContent.Create(new { question = "有給の繰越は？" })
        };
        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();

        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.EventType.Should().Be(UsageEventType.Answer);
    }

    // ★ 発火（分析）。契約の `answer` は「AI 回答生成」であって SC-01 の質問に限られていない。
    [Fact]
    public async Task PostAnalyze_WhenSucceeded_ReportsAnswerUsageEvent()
    {
        factory.ResetUsageEvents();

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/analysis/analyze",
            new { instruction = "四半期ごとに比較して", targets = new[] { "docA" } },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();

        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.EventType.Should().Be(UsageEventType.Answer);
    }

    // ★ 主体の解決。受け口は `RequireAuthorization()` を持ち、利用者を `HttpContext.User` から取る。
    // **発火点を BFF に置いた理由そのもの**であり、伝播が落ちると受け口は 401 を返して恒久的に 0 へ戻る。
    [Fact]
    public async Task ReportedUsageEvent_CarriesCallerCredentials()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "caller-token");
        var resp = await client.PostAsJsonAsync(
            "/bff/search", new { query = "就業規則", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();

        factory.RecordedUsageEvents.Should().ContainSingle()
            .Which.Authorization.Should().Be("Bearer caller-token",
                "受け口は利用者主体を JWT から解決する。伝播が落ちると 401 になり利用状況が 0 のままになる");
    }

    // ★ fail-open（受け口が非 2xx）。**計測の失敗で検索を落とさない。**
    [Fact]
    public async Task PostSearch_WhenUsageEndpointRejects_SearchStillSucceeds()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;
        factory.UsageEventStubStatusCode = HttpStatusCode.InternalServerError;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/search", new { query = "旅費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "計測の失敗で検索そのものを失敗させない");
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        body!.Results.Should().NotBeEmpty();

        // 送出は試みられている（拒否は結末であって未発火ではない）。**両者を混同させない。**
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    // ★ fail-open（受け口へ到達できない）。列挙した例外型だけを握る作りだと漏れるので、
    // **停止要求以外はすべて握る**（IADR-0336 決定 4）。
    [Fact]
    public async Task PostSearch_WhenUsageEndpointUnreachable_SearchStillSucceeds()
    {
        factory.ResetUsageEvents();
        factory.SearchScopeGranted = true;
        factory.UsageEventStubThrows = true;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/bff/search", new { query = "通勤手当", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "受け口へ到達できなくても検索は成功する");
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue();

        // 常駐ドレインが例外で死んでいないこと（次の 1 件が届く＝ホストごと落ちていない）。
        factory.UsageEventStubThrows = false;
        var next = await factory.CreateClient().PostAsJsonAsync(
            "/bff/search", new { query = "住宅手当", topK = 5 }, TestContext.Current.CancellationToken);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken)).Should().BeTrue(
            "1 件の到達不能でドレインを止めない");
    }

    // ★ 陰性対照。**実行されていない検索を利用状況に数えない。**
    // 空クエリ・許可スコープ無しは後段を呼ばずに空応答へ落ちるので、発火してはならない。
    //
    // 🔴 「届かなかった」を「発火しなかった」と読み違えないよう、**同じテストの中に陽性対照を置く**
    // —— 最後に本物の検索を 1 回流し、届くのがその 1 件だけであることを見る
    // （経路が丸ごと壊れていれば陽性対照が落ちる）。
    [Fact]
    public async Task PostSearch_WhenNoSearchExecuted_DoesNotReportUsageEvent()
    {
        factory.ResetUsageEvents();
        var client = factory.CreateClient();

        factory.SearchScopeGranted = true;
        var blank = await client.PostAsJsonAsync(
            "/bff/search", new { query = "   ", topK = 5 }, TestContext.Current.CancellationToken);
        blank.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.SearchScopeGranted = false;
        var denied = await client.PostAsJsonAsync(
            "/bff/search", new { query = "権限外", topK = 5 }, TestContext.Current.CancellationToken);
        denied.StatusCode.Should().Be(HttpStatusCode.OK);

        // 陽性対照。
        factory.SearchScopeGranted = true;
        var real = await client.PostAsJsonAsync(
            "/bff/search", new { query = "陽性対照", topK = 5 }, TestContext.Current.CancellationToken);
        real.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.WaitForUsageEventAsync(Arrival, TestContext.Current.CancellationToken))
            .Should().BeTrue("陽性対照が届かないなら、上の 2 件の不在は経路の故障でしか説明できない");

        factory.RecordedUsageEvents.Should().ContainSingle(
            "空クエリと許可スコープ無しは検索を実行していない")
            .Which.Query.Should().Be("陽性対照");
    }
}
