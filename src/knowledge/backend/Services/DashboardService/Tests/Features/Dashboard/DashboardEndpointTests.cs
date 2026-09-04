using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using DashboardService.Features.Dashboard;
using DashboardService.Features.Dashboard.RecordEvent;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Tests.Features.Dashboard;

// FR-10, UC-05: 利用状況・検索傾向・回答品質ダッシュボードのエンドポイントテスト。
// 集計はグローバル（テスト間で共有すると混ざる）ため、各テストは専用の InMemory DB
// （TestWebApplicationFactory を per-test 生成）で独立させる。
[Trait("TestKind", "Integration")]
public class DashboardEndpointTests
{
    // T-01: 検索イベントの記録で 201。
    [Fact]
    public async Task RecordSearchEvent_Creates()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "経費規程"), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-02: 回答イベントの記録で 201（大文字 "ANSWER" も正規化される）。
    [Fact]
    public async Task RecordAnswerEvent_Creates()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("ANSWER"), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-03: 不正な eventType は 400。
    //
    // FR-10 / IADR-0371 決定 2 / IADR-0377: 検証を FluentValidation へ移した際、
    // **状態コードだけでなく本文も変わっていない**ことを固定する ——
    // 400 のままメッセージだけが変わる退行は状態コードでは捕まらない。
    [Fact]
    public async Task InvalidEventType_Returns400WithOriginalBody()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("click"), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should()
            .Be(RecordUsageEventValidator.EventTypeInvalidMessage);
    }

    // T-04: 利用状況の集計（日次 × 種別の件数）。検索×2・回答×1 を投入し件数を確認する。
    [Fact]
    public async Task Usage_AggregatesByTypeAndDate()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "a"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "b"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("answer"), TestContext.Current.CancellationToken);

        // レスポンスが null の場合は素の NullReferenceException ではなく明示的な失敗にしてから
        // 非 null 変数へ確定させ、以降の拡張メソッド Where で CS8604 を誘発しないようにする。
        var response = await client.GetFromJsonAsync<List<UsagePointDto>>("/dashboard/usage", TestContext.Current.CancellationToken);
        response.Should().NotBeNull();
        var points = response!;

        points.Where(p => p.EventType == "search").Sum(p => p.Count).Should().Be(2);
        points.Where(p => p.EventType == "answer").Sum(p => p.Count).Should().Be(1);
    }

    // T-05: 検索傾向は件数降順の上位語を返す。
    //
    // **［#1197］両方の語をしきい値（既定 3）以上へ引き上げた。** 従前は 経費×3 / 有給×1 で
    // 「2 件以上返る」ことを確かめていたが、**ADR-0071 決定 1 の下限述語が入ると 有給 は落ちる**。
    // ここで確かめたいのは**並び順**であり、秘匿ではない（秘匿は T-63 以降が対で押さえる）。
    [Fact]
    public async Task Trends_ReturnsTopTermsByCount()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "経費"), TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "有給"), TestContext.Current.CancellationToken);

        var trendsResponse = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trendsResponse.Should().NotBeNull();
        var trends = trendsResponse!;

        trends.Should().HaveCount(2);
        trends[0].Term.Should().Be("経費");
        trends[0].Count.Should().Be(4);
        trends[1].Term.Should().Be("有給");
    }

    // T-06: 検索語は前後空白除去・小文字化で正規化され、同一語として集計される。
    [Fact]
    public async Task Trends_NormalizesTerms()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "Foo"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", " foo "), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "FOO"), TestContext.Current.CancellationToken);

        var trendsResponse = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trendsResponse.Should().NotBeNull();
        var trends = trendsResponse!;

        trends.Should().ContainSingle();
        trends[0].Term.Should().Be("foo");
        trends[0].Count.Should().Be(3);
    }

    // T-07: サマリは総件数・利用状況・検索傾向を集約して返す。
    //
    // **［#1197］検索語の投入を 2 回 → 3 回にした。** 2 回では ADR-0071 決定 1 の下限で伏せられる。
    // **総件数（TotalSearches）はしきい値の影響を受けない** —— 伏せるのは**語**であって件数ではない
    // （ADR-0071 決定 1 は上位一覧の話であり、利用状況の指標には及ばない）。ここも併せて固定する。
    [Fact]
    public async Task Summary_AggregatesUsageAndTrends()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "契約"), TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("answer"), TestContext.Current.CancellationToken);

        var summaryResponse = await client.GetFromJsonAsync<DashboardUsageDto>("/dashboard/summary", TestContext.Current.CancellationToken);
        summaryResponse.Should().NotBeNull();
        var summary = summaryResponse!;

        summary.TotalSearches.Should().Be(3);
        summary.TotalAnswers.Should().Be(1);
        summary.TopSearchTerms.Should().ContainSingle(t => t.Term == "契約" && t.Count == 3);
    }

    // ───────────────────────────────────────────────────────────────────────
    // FR-10, SC-10, ADR-0071 決定 1・2（#1197）: 検索傾向の出現件数しきい値。
    //
    // **陽性と陰性を対で置く。** 片方だけだと検査にならない —— 下限述語を丸ごと外しても
    // 「3 件の語が出る」テストは緑のまま通る（変異試験で確認した）。
    // ───────────────────────────────────────────────────────────────────────

    // T-63 ★ **陰性**: しきい値（既定 3）未満の語は応答に含まれない。
    [Fact]
    public async Task Trends_BelowMinimumCount_AreOmitted()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        // 3 件の語（対照）と 2 件の語（伏せる対象）を同じ期間へ入れる。
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "就業規則"), TestContext.Current.CancellationToken);
        for (var i = 0; i < 2; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "田中の評価面談メモ"), TestContext.Current.CancellationToken);

        var trends = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();

        trends!.Should().NotContain(t => t.Term == "田中の評価面談メモ",
            "ADR-0071 決定 1 は出現件数 3 件未満の語を上位一覧に出さないと定めている");
        // **陽性対照**: 走査そのものが空振りしていない（3 件の語は出る）。
        trends.Should().Contain(t => t.Term == "就業規則" && t.Count == 3);
    }

    // T-64 ★ **境界**: ちょうど 3 件は**含まれる**（`>=` であって `>` ではない）。
    // T-63 と対で、境界 3 を上下から固定する。
    [Fact]
    public async Task Trends_AtMinimumCount_AreIncluded()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "経費規程"), TestContext.Current.CancellationToken);

        var trends = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();

        trends!.Should().ContainSingle(t => t.Term == "経費規程" && t.Count == 3);
    }

    // T-65 ★ **「その他 M 件」を出さない**（ADR-0071 決定 1。M 自体が推測の材料になる）。
    // しきい値未満の語だけが 5 種類ある期間で、応答は**空**になる。
    [Fact]
    public async Task Trends_OmittedTerms_AreNotAggregatedIntoAnOtherRow()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        foreach (var term in new[] { "語1", "語2", "語3", "語4", "語5" })
            for (var i = 0; i < 2; i++)
                await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", term), TestContext.Current.CancellationToken);

        var trends = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();

        trends!.Should().BeEmpty(
            "伏せた語は落とす。『その他 5 件』に相当する項目も出さない（M が推測の材料になる）");
    }

    // T-66 ★ **配備時の構成で変更できる**（ADR-0071 決定 1 末尾）。
    // しきい値 5 では 4 件の語も伏せられる。**宣言だけでは検査にならないので実測する。**
    [Fact]
    public async Task Trends_MinimumCount_IsConfigurable()
    {
        using var factory = new TestWebApplicationFactory
        {
            Settings = new Dictionary<string, string?> { ["SearchTrend:MinimumCount"] = "5" }
        };
        var client = factory.CreateClient();
        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "稟議"), TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "出張"), TestContext.Current.CancellationToken);

        var trends = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends", TestContext.Current.CancellationToken);
        trends.Should().NotBeNull();

        trends!.Should().NotContain(t => t.Term == "稟議", "構成したしきい値 5 に満たない");
        // **陽性対照**: 構成が効いていないのではなく、4 件だから落ちている。
        trends.Should().Contain(t => t.Term == "出張" && t.Count == 5);
    }

    // T-67 ★ サマリは**現在のしきい値**を 1 項目として返す（ADR-0071 決定 2。画面の併記の材料）。
    [Theory]
    [InlineData(null, 3)]   // 未指定 = appsettings.json の既定 3
    [InlineData("5", 5)]    // 構成した値がそのまま出る
    public async Task Summary_ReportsCurrentMinimumCount(string? configured, int expected)
    {
        using var factory = new TestWebApplicationFactory
        {
            Settings = configured is null
                ? null
                : new Dictionary<string, string?> { ["SearchTrend:MinimumCount"] = configured }
        };

        var summary = await factory.CreateClient()
            .GetFromJsonAsync<DashboardUsageDto>("/dashboard/summary", TestContext.Current.CancellationToken);
        summary.Should().NotBeNull();

        summary!.SearchTermMinCount.Should().Be(expected);
    }

    // T-68 ★ **不正な構成で起動を落とさず、既定へ倒す。報告する値も倒した後の値**
    // （[[IADR-0357]] 決定 4）。構成値 0 をそのまま返すと、**見える語（3 件以上）と
    // 併記された数字（0）が食い違い、画面が嘘をつく。**
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Summary_InvalidMinimumCount_FallsBackToDefault(string configured)
    {
        using var factory = new TestWebApplicationFactory
        {
            Settings = new Dictionary<string, string?> { ["SearchTrend:MinimumCount"] = configured }
        };
        var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "私信"), TestContext.Current.CancellationToken);

        var summary = await client.GetFromJsonAsync<DashboardUsageDto>("/dashboard/summary", TestContext.Current.CancellationToken);
        summary.Should().NotBeNull();

        summary!.SearchTermMinCount.Should().Be(SearchTrendOptions.DefaultMinimumCount);
        // 倒した後の値で**実際にふるっている**（宣言と挙動が一致する）。
        summary.TopSearchTerms.Should().NotContain(t => t.Term == "私信");
    }

    // ★ #544: **運用者が集計を引ける**（受け入れ基準 1 のサービス層。[[IADR-0044]] の多層防御）。
    //
    // **BFF 側のテストでは代わりにならない** —— BFF を迂回した直接呼び出しでも
    // 同じ範囲が効くことを、ここで独立に押さえる。
    [Theory]
    [InlineData("/dashboard/usage")]
    [InlineData("/dashboard/trends")]
    [InlineData("/dashboard/summary")]
    public async Task Aggregates_AsOperator_AreAllowed(string path)
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "計画 §SC-10 は閲覧を運用者・管理者ロール限定と定めている（裁定 Q19 / Q28）");
    }

    // T-08: ★ **管理系ロール以外では 403**（受け入れ基準 2「広げすぎない」）。
    //
    // **［#544］名前と趣旨を実態へ揃えた。** 従前は `Usage_WithoutAdminRole_Returns403`
    // ＋「AdminOnly。非管理ロールは 403」だったが、**運用者は管理者ではないのに 200 になった**ので
    // その言い方は誤りになった。検証しているのは「**管理系ロール以外**は 403」である。
    [Theory]
    [InlineData("/dashboard/usage")]
    [InlineData("/dashboard/trends")]
    [InlineData("/dashboard/summary")]
    public async Task Aggregates_WithoutPrivilegedRole_Return403(string path)
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer"); // 管理系以外のロールを明示。

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-09: イベント記録は認証済みなら管理系ロール以外でも可能（集計の入力を絞らない）。
    // **［#544］本作業で触っていない** —— 参照専用であり、書き込み権限は広げない。
    [Fact]
    public async Task RecordEvent_AllowedForNonAdmin()
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/events")
        {
            Content = JsonContent.Create(new UsageEventRequest("search", "検索語"))
        };
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await factory.CreateClient().SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
