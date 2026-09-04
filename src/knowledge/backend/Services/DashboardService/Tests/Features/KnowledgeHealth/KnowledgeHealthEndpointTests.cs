using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DashboardService.Features.KnowledgeHealth;
using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Features.KnowledgeHealth.View;
using DashboardService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardService.Tests.Features.KnowledgeHealth;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006 (#443): ナレッジ健全性指標。
// 計画 §ナレッジ健全性の指標 は **ABAC の文書単位判定に対する明示的な例外**として全体集計を許すが、
// **件数のみ・ロール限定・個人資料除外の 3 つを同時に満たすことが条件**である。
// 3 つのうち 1 つでも欠けると存在秘匿が崩れるため、**それぞれを独立にテストで固定する**。
[Trait("TestKind", "Integration")]
public class KnowledgeHealthEndpointTests
{
    // 🔴 送信側 GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter.ObservationsPath の値。
    // **サービスを跨ぐため定数を共有できない**。**リテラルで持ち、一致を下のテストで固定する**
    // （`/internal/notifications` の送信側・受け口と同じ作法）。
    private const string ProducerObservationsPath = "/internal/knowledge-health/observations";

    private static KnowledgeHealthReportRequest Report(
        string indicator, params KnowledgeHealthObservationRequest[] observations)
        => new(indicator, observations);

    // FR-10, FR-17 (T-20): 観測値を報告すると、指標ごとの件数として集計される。
    [Fact]
    public async Task 報告した観測値が指標ごとの件数として集計される()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
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

        await client.PostAsJsonAsync(ProducerObservationsPath,
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

        await client.PostAsJsonAsync(ProducerObservationsPath,
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
        await client.PostAsJsonAsync(ProducerObservationsPath,
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
        await client.PostAsJsonAsync(ProducerObservationsPath,
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

        await client.PostAsJsonAsync(ProducerObservationsPath,
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1"),
                new KnowledgeHealthObservationRequest("doc-2")),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(ProducerObservationsPath,
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
    //
    // FR-10 / IADR-0371 決定 2 / IADR-0376: 検証を FluentValidation へ移した際、
    // **状態コードだけでなく本文も変わっていない**ことを固定する ——
    // 400 のままメッセージだけが変わる退行（あるいは規則順が入れ替わって別の理由が返る退行）は
    // 状態コードでは捕まらない。
    [Fact]
    public async Task 未知の指標名は400()
    {
        using var factory = new TestWebApplicationFactory();

        var resp = await factory.CreateClient().PostAsJsonAsync(
            ProducerObservationsPath,
            Report("orphan-docs", new KnowledgeHealthObservationRequest("doc-1")),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should()
            .Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
    }

    // ── 受け口の移設（#443 / [[IADR-0299]] 決定 4） ───────────────────────────

    // FR-10, FR-17 (T-29): 🔴 **パスの複製が食い違っていないことを固定する。**
    // サービス間の直接参照が張れないため、この一致を守る機械はこのテストと送信側のテストだけである。
    [Fact]
    public async Task 受け口のパスは生産者側の宣言と同じ値である()
    {
        ReportKnowledgeHealthEndpoint.ObservationsPath.Should().Be(ProducerObservationsPath,
            "★ 送信側 HttpKnowledgeHealthReporter.ObservationsPath と 1 バイトでも違えば観測値は届かない"
            + "（送出は fail-open のため、不一致は 404 のログにしか現れない）");

        var resp = await new TestWebApplicationFactory().CreateClient().PostAsJsonAsync(
            ProducerObservationsPath,
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1", "organization")),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "宣言したパスで到達できること");
    }

    // FR-10, FR-17 (T-30): 🔴 **受け口は認可を要求せず、OpenAPI にも載らない。**
    // 生産者は**利用者 JWT を持たない定期処理**であり、client_credentials の実装は本体に 1 行も無い。
    // `/internal/notifications` と同じ形を採った（[[IADR-0299]] 決定 4。利用者裁定）。
    //
    // **統制は mTLS ＋ ネットワーク分離であって、認可ではない。** ここで固定するのは
    // 「認可メタデータが付いていないこと」＝**移設が巻き戻っていないこと**である
    // （テスト器の認証ハンドラは常に認証を成功させるため、無認証の到達性は状態コードでは測れない）。
    [Fact]
    public void 観測値の受け口は認可を要求せずOpenAPIにも載せない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var endpoint = FindByName(factory, "ReportKnowledgeHealth");

        endpoint.RoutePattern.RawText.Should().Be(ProducerObservationsPath,
            "移設先のパスが `/internal/...` のままであること");
        endpoint.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull(
            "生産者は利用者 JWT を持たない定期処理である（認可を課すと観測値が永久に届かない）");
        endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()
            .Should().NotBeNull("内部 API は OpenAPI に載せない（/internal/* は 1 本も無い）");
    }

    // FR-10, SC-10 (T-31): 🔴 **閲覧側のロール限定は移設で緩んでいない。**
    // 全体集計を許す条件は「件数のみ・ロール限定・個人資料除外」の同時成立であり、
    // 受け口を無認証にしたこととは独立である。**両方を同じ PR で動かしたので、両方を固定する。**
    [Fact]
    public void 閲覧側はロール限定のままである()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var endpoint = FindByName(factory, "KnowledgeHealth");

        endpoint.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull(
            "閲覧側のロール制限が唯一の統制点である（計画 §ナレッジ健全性の指標 規則 2）");
    }

    // FR-10, FR-17, FR-19 (T-32): 🔴 **生産者が実際に送る JSON がそのまま束縛できる。**
    // 型で書いた Report(...) は**両側が同じ DTO を使う前提**を暗黙に置いており、
    // 送信側が匿名オブジェクトを組み立てている以上、**綴りと大小の一致は型では守られない**
    // （`docScope` を `docscope` と書いても C# は何も言わない）。生の JSON で固定する。
    [Fact]
    public async Task 生産者が組み立てる生のJSONで束縛できる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        // HttpKnowledgeHealthReporter が PostAsJsonAsync へ渡す匿名オブジェクトと同じ形。
        const string body = """
            {"indicator":"orphan-documents","observations":[
              {"subjectKey":"11111111-1111-1111-1111-111111111111","docScope":null},
              {"subjectKey":"22222222-2222-2222-2222-222222222222","docScope":"private-note"}]}
            """;

        var resp = await client.PostAsync(ProducerObservationsPath,
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.OrphanDocuments)
            .Count.Should().Be(1, "個人資料は受け手が落とす（生の JSON でも綴りが噛み合っている）");
    }

    // ── しきい値の併記（planning#494 決定 3 / [[IADR-0353]] 決定 4。#1186） ──

    // FR-10, SC-10 (T-57): 生産者が添えたしきい値が件数と一緒に返る。
    // 計画は「SC-10 には**件数と現在のしきい値を併記する**」と定めており、
    // **件数だけでは同じ数字の意味が配備ごとに違ってしまう**。
    [Fact]
    public async Task 報告に添えられたしきい値が件数と併せて返る()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(
                KnowledgeHealthIndicators.StaleDocuments,
                [new KnowledgeHealthObservationRequest("doc-1", "organization")],
                ThresholdDays: 180),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var stale = health!.Indicators.Single(i =>
            i.Indicator == KnowledgeHealthIndicators.StaleDocuments);
        stale.Count.Should().Be(1);
        stale.ThresholdDays.Should().Be(180);
    }

    // 🔴 FR-10, SC-10 (T-58): **件数 0 でもしきい値は返る。**
    // ここが本設計の要点である —— しきい値を観測値の行へ持たせると、
    // **0 件のときに 1 行も無く、しきい値も一緒に消える**。0 件こそ表示したい状態である。
    [Fact]
    public async Task 件数が0でもしきい値は返る()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(
                KnowledgeHealthIndicators.StaleDocuments, [], ThresholdDays: 90),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var stale = health!.Indicators.Single(i =>
            i.Indicator == KnowledgeHealthIndicators.StaleDocuments);
        stale.Count.Should().Be(0);
        stale.ThresholdDays.Should().Be(90, "🔴 0 件のときこそしきい値が要る");
    }

    // FR-10, SC-10 (T-59): **陰性対照。** しきい値を持たない指標では null である
    // （全指標へ一律の既定値を埋めない —— 意味の無いしきい値が画面へ出る）。
    [Fact]
    public async Task しきい値を持たない指標のしきい値はnullである()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1", "organization")),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.OrphanDocuments)
            .ThresholdDays.Should().BeNull();
    }

    // FR-10 (T-60): しきい値も**スナップショット置換**である。
    // 添えない報告が来たら行を消す —— 残すと、生産者が変わった後も古い日数が出続ける。
    [Fact]
    public async Task しきい値を添えない報告は既存のしきい値を落とす()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(
                KnowledgeHealthIndicators.StaleDocuments, [], ThresholdDays: 180),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(ProducerObservationsPath,
            Report(KnowledgeHealthIndicators.StaleDocuments),
            TestContext.Current.CancellationToken);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.StaleDocuments)
            .ThresholdDays.Should().BeNull();
    }

    // FR-10 (T-61): **0 以下のしきい値は 400。** 保存すると画面が「しきい値 0 日」と表示し、
    // 件数の意味が読めなくなる（指標名の値域を閉じているのと同じ姿勢）。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ゼロ以下のしきい値は400になる(int days)
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(ProducerObservationsPath,
            new KnowledgeHealthReportRequest(
                KnowledgeHealthIndicators.StaleDocuments, [], ThresholdDays: days),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // IADR-0371 決定 2 / IADR-0376: 本文も移送前と同じであること。
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should()
            .Be(ReportKnowledgeHealthValidator.ThresholdInvalidMessage);
    }

    // FR-10 (T-62): 🔴 **生産者が実際に送る JSON がそのまま束縛できる**（T-32 と同じ作法）。
    // 送っているのは匿名オブジェクトであり、`thresholdDays` の綴り違いを C# は何も言わない。
    [Fact]
    public async Task 生産者が送る陳腐化のJSONがそのまま束縛できる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        // GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter が組む本文。
        var body = """
            {"indicator":"stale-documents",
             "observations":[{"subjectKey":"doc-1","docScope":null}],
             "thresholdDays":180}
            """;

        var resp = await client.PostAsync(ProducerObservationsPath,
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);

        var stale = health!.Indicators.Single(i =>
            i.Indicator == KnowledgeHealthIndicators.StaleDocuments);
        stale.Count.Should().Be(1);
        stale.ThresholdDays.Should().Be(180, "綴りが噛み合っていないと null のまま静かに通る");
    }

    // 実 Program.cs の配線から**名前**で終端を引く（ルートパターンの生文字列は
    // MapGroup の合成のされ方に依存し、パスの検査そのものは別の assert で行う）。
    private static RouteEndpoint FindByName(TestWebApplicationFactory factory, string name)
        => factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
}
