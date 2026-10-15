using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DashboardService.Tests.Grpc;
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
    // ［2026-10-15 / #1902・[[IADR-0533]]］観測値の REST の受け口（`POST /internal/knowledge-health/observations`）は
    // 撤去した。生産者（graph の H-1）は east-west gRPC だけになっていた。投入は gRPC 面（`TestServerReportGrpc`）で行い、
    // 状態は REST の 202 / 400 → OK / INVALID_ARGUMENT、本文の `error` → status の detail に写る。
    private const string RemovedObservationsPath = "/internal/knowledge-health/observations";

    private static KnowledgeHealthReportRequest Report(
        string indicator, params KnowledgeHealthObservationRequest[] observations)
        => new(indicator, observations);

    // FR-10, FR-17 (T-20): 観測値を報告すると、指標ごとの件数として集計される。
    [Fact]
    public async Task 報告した観測値が指標ごとの件数として集計される()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        await client.ReportAsync(Report(KnowledgeHealthIndicators.OrphanDocuments,
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

        await client.ReportAsync(Report(KnowledgeHealthIndicators.OrphanDocuments,
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

        await client.ReportAsync(Report(KnowledgeHealthIndicators.UnresolvedLinks,
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
        await client.ReportAsync(Report(KnowledgeHealthIndicators.OrphanDocuments,
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
        await client.ReportAsync(Report(KnowledgeHealthIndicators.StaleDocuments,
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

        await client.ReportAsync(Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1"),
                new KnowledgeHealthObservationRequest("doc-2")),
            TestContext.Current.CancellationToken);
        await client.ReportAsync(Report(KnowledgeHealthIndicators.OrphanDocuments,
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
    // FR-10 / IADR-0371 決定 2 / IADR-0393: 検証を FluentValidation へ移した際、
    // **状態コードだけでなく本文も変わっていない**ことを固定する ——
    // 400 のままメッセージだけが変わる退行（あるいは規則順が入れ替わって別の理由が返る退行）は
    // 状態コードでは捕まらない。
    [Fact]
    public async Task 未知の指標名はINVALID_ARGUMENT()
    {
        using var factory = new TestWebApplicationFactory();

        var resp = await factory.CreateClient().ReportAsync(Report("orphan-docs", new KnowledgeHealthObservationRequest("doc-1")),
            TestContext.Current.CancellationToken);

        resp.Status.Should().Be(global::Grpc.Core.StatusCode.InvalidArgument);
        resp.Detail.Should().Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
    }

    // ── 受け口（#443 / [[IADR-0299]] 決定 4 → #1902 で gRPC だけ） ───────────────────

    // FR-10, FR-17 (T-29 / T-30): ［2026-10-15 / #1902・[[IADR-0533]]］無認証の REST の受け口は撤去した。
    // 従前は「パスが生産者の宣言と同じ」「認可を要求せず OpenAPI に載せない」を固定していた —— 生産者は gRPC
    // （`ServiceCaller`）だけになり、無認証の口を残す理由が無くなった。撤去したことをルート表と到達で固定する
    // （戻ると、同一ネットワーク内から無認証で観測値を差し替えられる口が増える。[[IADR-0299]] の残余リスク）。
    [Fact]
    public async Task 撤去したRESTの受け口はルート表に無い()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Should().NotContain(e => e.RoutePattern.RawText == RemovedObservationsPath);

        var resp = await client.PostAsJsonAsync(RemovedObservationsPath,
            Report(KnowledgeHealthIndicators.OrphanDocuments,
                new KnowledgeHealthObservationRequest("doc-1", "organization")),
            TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    // FR-10, FR-17, FR-19 (T-32): 🔴 **生産者が組む形がそのまま束縛できる**（しきい値を持たない指標は threshold_days を送らない）。
    // ［2026-10-15 / #1902］従前は旧 REST の生の JSON（綴りと大小）を固定していた。REST の受け口を撤去したので、
    // 生産者（gRPC）の proto の形で固定する —— `has` を読まずに素の値を読むと、未指定のしきい値が 0 に化けて弾かれる。
    [Fact]
    public async Task 生産者が組み立てる形で束縛できる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var request = new Knowledge.Contracts.Grpc.Dashboard.V1.ReportRequest { Indicator = "orphan-documents" };
        request.Observations.Add(new Knowledge.Contracts.Grpc.Dashboard.V1.Observation
        { SubjectKey = "11111111-1111-1111-1111-111111111111" });
        request.Observations.Add(new Knowledge.Contracts.Grpc.Dashboard.V1.Observation
        { SubjectKey = "22222222-2222-2222-2222-222222222222", DocScope = "private-note" });

        var resp = await client.ReportAsync(request, TestContext.Current.CancellationToken);
        resp.Status.Should().Be(global::Grpc.Core.StatusCode.OK);

        var health = await client.GetFromJsonAsync<KnowledgeHealthDto>(
            "/dashboard/knowledge-health", TestContext.Current.CancellationToken);
        health!.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.OrphanDocuments)
            .Count.Should().Be(1, "個人資料の印は proto の doc_scope で届き、閲覧側で除外される");
        health.Indicators.Single(i => i.Indicator == KnowledgeHealthIndicators.StaleDocuments)
            .ThresholdDays.Should().BeNull();
    }

    // FR-10 (T-61): **0 以下のしきい値は INVALID_ARGUMENT**（REST の 400）。 保存すると画面が「しきい値 0 日」と表示し、
    // 件数の意味が読めなくなる（指標名の値域を閉じているのと同じ姿勢）。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ゼロ以下のしきい値はINVALID_ARGUMENTになる(int days)
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var resp = await client.ReportAsync(new KnowledgeHealthReportRequest(
                KnowledgeHealthIndicators.StaleDocuments, [], ThresholdDays: days),
            TestContext.Current.CancellationToken);

        resp.Status.Should().Be(global::Grpc.Core.StatusCode.InvalidArgument);
        // IADR-0371 決定 2 / IADR-0393: 理由の本文も変わっていないこと。
        resp.Detail.Should().Be(ReportKnowledgeHealthValidator.ThresholdInvalidMessage);
    }

    // FR-10, FR-17, FR-18 / IADR-0371 決定 2 / IADR-0393 決定 3 / [[IADR-0409]] (#1230):
    // 🔴 **規則の宣言順が応答の契約であることを、輸送越しに固定する。**
    //
    // 既存の `ReportKnowledgeHealthValidatorTests.MultipleViolations_ReportsIndicatorFirst` は
    // **検証器を直接呼ぶ**ので、`ReportKnowledgeHealthUseCase.Validate` が `Errors[0]` を採ることは
    // 通らない。違反が 1 件のときは `Errors[0]` と `Errors[^1]` が同じ要素を指すため、
    // **単一違反のテストだけでは添字の変更を区別できない** ——
    // 実測（#1230）: 本テスト新設前は同ファイルの添字を `Errors[^1]` へ変えても全 90 試験が緑だった。
    //
    // **指標が未知 ＋ しきい値 0** が両規則に触れる唯一の作り方である。
    [Fact]
    public async Task 指標としきい値が同時に不正でも最初の規則の本文が返る()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var resp = await client.ReportAsync(new KnowledgeHealthReportRequest("orphan-docs", [], ThresholdDays: 0),
            TestContext.Current.CancellationToken);

        resp.Status.Should().Be(global::Grpc.Core.StatusCode.InvalidArgument);
        resp.Detail.Should().Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
    }

    // FR-10 (T-62): 🔴 **生産者が送る陳腐化の形がそのまま束縛できる**（T-32 と同じ作法）。
    // `threshold_days` を `has` で読まないと、送ったしきい値が null として静かに落ちる。
    [Fact]
    public async Task 生産者が送る陳腐化の形がそのまま束縛できる()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var request = new Knowledge.Contracts.Grpc.Dashboard.V1.ReportRequest
        { Indicator = "stale-documents", ThresholdDays = 180 };
        request.Observations.Add(new Knowledge.Contracts.Grpc.Dashboard.V1.Observation { SubjectKey = "doc-1" });

        var resp = await client.ReportAsync(request, TestContext.Current.CancellationToken);
        resp.Status.Should().Be(global::Grpc.Core.StatusCode.OK);

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
