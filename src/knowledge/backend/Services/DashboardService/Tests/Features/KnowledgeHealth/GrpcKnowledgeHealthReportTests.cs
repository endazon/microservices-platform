using System.Net.Http.Json;
using AwesomeAssertions;
using DashboardService.Domain;
using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Infrastructure.Persistence;
using DashboardService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Dashboard.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace DashboardService.Tests.Features.KnowledgeHealth;

// FR-10, FR-17, FR-18, FR-19, NFR-09, NFR-16, UC-05, SC-10, ADR-0002, ADR-0006, ADR-0029, ADR-0075,
// [[IADR-0256]] 決定 3, [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0379]], [[IADR-0389]],
// [[IADR-0402]], [[IADR-0408]] (#1255): 観測値の受け口の gRPC 面
// （`knowledge.dashboard.v1.KnowledgeHealthReport`）を**実 Kestrel の h2c ポート**で往復し、
// s2s トークンの検証・REST との同値・proto3 の「未指定」の写しを固定する。
//
// 陽性対照（T-01）と陰性対照（T-04 / T-05）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcKnowledgeHealthReportTests
{
    private const string ServiceSubject = "service-account-graph-service";

    // 🔴 送信側 GraphService.Infrastructure.ExternalServices.HttpKnowledgeHealthReporter.ObservationsPath の値。
    // **サービスを跨ぐため定数を共有できない**。リテラルで持ち、一致を両側のテストで固定する。
    private const string ProducerObservationsPath = "/internal/knowledge-health/observations";

    private readonly GrpcKestrelFactory _factory;

    public GrpcKnowledgeHealthReportTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private KnowledgeHealthReport.KnowledgeHealthReportClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private async Task<(List<KnowledgeHealthObservation> Observations, KnowledgeHealthIndicatorThreshold? Threshold)>
        ReadAsync(string indicator)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var observations = await db.KnowledgeHealthObservations.AsNoTracking()
            .Where(o => o.Indicator == indicator).ToListAsync(ct);
        var threshold = await db.KnowledgeHealthIndicatorThresholds.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Indicator == indicator, ct);
        return (observations, threshold);
    }

    // T-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで
    // 往復し、観測値がスナップショットとして保存され、応答が REST の 202 本文と同形で返る。
    [Fact]
    public async Task Report_over_h2c_with_service_token_replaces_the_snapshot()
    {
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new KnowledgeHealthReport.KnowledgeHealthReportClient(channel);

        var request = new ReportRequest { Indicator = KnowledgeHealthIndicators.OrphanDocuments };
        request.Observations.Add(new Observation { SubjectKey = "grpc-doc-1" });
        request.Observations.Add(new Observation { SubjectKey = "grpc-doc-2", DocScope = "private-note" });

        var resp = await client.ReportAsync(
            request, cancellationToken: TestContext.Current.CancellationToken);

        resp.Indicator.Should().Be(KnowledgeHealthIndicators.OrphanDocuments);
        resp.Accepted.Should().Be(2);

        var (observations, _) = await ReadAsync(KnowledgeHealthIndicators.OrphanDocuments);
        observations.Select(o => o.SubjectKey).Should().BeEquivalentTo(["grpc-doc-1", "grpc-doc-2"]);
        observations.Single(o => o.SubjectKey == "grpc-doc-2").DocScope.Should().Be("private-note");
    }

    // 🔴 T-02: **`threshold_days` の presence。** REST は「項目そのものを出さない」ことで
    // 「しきい値なし」を表し、受け口はしきい値の行を**削除**する。proto3 の既定（0）へ潰れると
    // 検証器が `thresholdDays must be greater than zero` で弾き、**しきい値を持たない 3 指標
    // （orphan-documents / unresolved-links / edge-type-usage）の報告が全部落ちる**。
    // 陽性（添えたら保存される）と陰性（添えないと消える）を対で固定する。
    [Fact]
    public async Task ThresholdDays_presence_distinguishes_absent_from_zero()
    {
        var client = PlainClient();
        var ct = TestContext.Current.CancellationToken;
        var indicator = KnowledgeHealthIndicators.StaleDocuments;

        // 陽性: 添えれば保存される。
        var withThreshold = new ReportRequest { Indicator = indicator, ThresholdDays = 180 };
        withThreshold.Observations.Add(new Observation { SubjectKey = "stale-1" });
        await client.ReportAsync(withThreshold, headers: Bearer(ServiceToken()), cancellationToken: ct);

        var (_, saved) = await ReadAsync(indicator);
        saved.Should().NotBeNull();
        saved!.ThresholdDays.Should().Be(180);

        // 陰性: 添えなければ **400 にならず**、しきい値の行が消える（0 として保存されない）。
        var withoutThreshold = new ReportRequest { Indicator = indicator };
        withoutThreshold.Observations.Add(new Observation { SubjectKey = "stale-1" });
        var resp = await client.ReportAsync(
            withoutThreshold, headers: Bearer(ServiceToken()), cancellationToken: ct);

        resp.Accepted.Should().Be(1, "しきい値を持たない報告が拒まれない");
        var (_, cleared) = await ReadAsync(indicator);
        cleared.Should().BeNull("添えられていなければ行を消す（古い日数が画面に残らない）");
    }

    // 🔴 T-03: **`doc_scope` / `dimension` の presence。** 未設定（null）と空文字は別物である ——
    // `""` を書くと台帳では「個人資料ではない」（null）と区別できず、内訳の軸には
    // `""` という軸が 1 本生まれる。
    [Fact]
    public async Task DocScope_and_dimension_absent_are_stored_as_null()
    {
        var client = PlainClient();
        var ct = TestContext.Current.CancellationToken;
        var indicator = KnowledgeHealthIndicators.EdgeTypeUsage;

        var request = new ReportRequest { Indicator = indicator };
        request.Observations.Add(new Observation { SubjectKey = "軸なし" });
        request.Observations.Add(new Observation { SubjectKey = "軸あり", Dimension = "relates-to" });
        await client.ReportAsync(request, headers: Bearer(ServiceToken()), cancellationToken: ct);

        var (observations, _) = await ReadAsync(indicator);
        var without = observations.Single(o => o.SubjectKey == "軸なし");
        without.DocScope.Should().BeNull("未設定は空文字ではない");
        without.Dimension.Should().BeNull("未設定は空文字ではない");
        observations.Single(o => o.SubjectKey == "軸あり").Dimension
            .Should().Be("relates-to", "陽性対照: 添えた軸は運ばれる");
    }

    // T-06: 値域外の指標は `INVALID_ARGUMENT`（REST の 400 と同値）。メッセージも同じ式から作る。
    // 🔴 **`INTERNAL` に化けさせない** —— 呼び出し元は縮退のときログしか残さないので、
    // 「要求の誤り」と「受け口の故障」が同じ status になると原因を切り分けられない。
    [Fact]
    public async Task Unknown_indicator_is_invalid_argument()
    {
        var act = async () => await PlainClient().ReportAsync(
            new ReportRequest { Indicator = "made-up-indicator" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Be(ReportKnowledgeHealthValidator.IndicatorInvalidMessage);
    }

    // T-06（しきい値側）: 0 以下は `INVALID_ARGUMENT`。**「未指定」とは別の事実**である。
    [Fact]
    public async Task Non_positive_threshold_is_invalid_argument()
    {
        var act = async () => await PlainClient().ReportAsync(
            new ReportRequest { Indicator = KnowledgeHealthIndicators.StaleDocuments, ThresholdDays = 0 },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Be(ReportKnowledgeHealthValidator.ThresholdInvalidMessage);
    }

    // T-04: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Report_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().ReportAsync(
            new ReportRequest { Indicator = KnowledgeHealthIndicators.OrphanDocuments },
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-05: **利用者トークンの転送を機械で止める唯一の点。**
    //
    // REST の受け口は**認証を持たない**（[[IADR-0299]] 決定 4・利用者裁定）ので、
    // 「管理者なら通る」形にすると s2s の面が利用者トークンでも開く。開くと呼び出し先は
    // 「利用者が直接呼んだ」と区別できず confused deputy が成立する（[[IADR-0379]] 決定 4）。
    // **管理者の利用者トークンでも PERMISSION_DENIED** であることを固定する。
    [Fact]
    public async Task Report_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().ReportAsync(
            new ReportRequest { Indicator = KnowledgeHealthIndicators.OrphanDocuments },
            headers: Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-07 ＋ T-08: REST と gRPC が**同じ本体**（`ReportKnowledgeHealthUseCase`）を通ることの観測。
    //
    // 🔴 REST が同じプロセスの HTTP/1.1 ポートで応えること自体が、**h2c を有効にしても
    // 8080 側が消えていない**ことの証明でもある（`AddPlatformGrpcListener` の 🔴）。
    //
    // 🔴 REST の受け口は**無認証**で通り、gRPC は s2s トークンを要る ——
    // 面ごとに通る資格情報が違うことが、そのまま「利用者トークンを転送していない」ことの現れである。
    [Fact]
    public async Task Rest_and_grpc_produce_the_same_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var indicator = KnowledgeHealthIndicators.UnresolvedLinks;
        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };

        // REST で置換する（無認証で通る）。
        var restResp = await http.PostAsJsonAsync(ProducerObservationsPath, new KnowledgeHealthReportRequest(
            indicator,
            [new KnowledgeHealthObservationRequest("link-a", null, "not-found"),
             new KnowledgeHealthObservationRequest("link-b", null, "ambiguous")]), ct);
        restResp.EnsureSuccessStatusCode();
        var (viaRest, _) = await ReadAsync(indicator);

        // gRPC で同じ内容を置換する。
        var request = new ReportRequest { Indicator = indicator };
        request.Observations.Add(new Observation { SubjectKey = "link-a", Dimension = "not-found" });
        request.Observations.Add(new Observation { SubjectKey = "link-b", Dimension = "ambiguous" });
        await PlainClient().ReportAsync(request, headers: Bearer(ServiceToken()), cancellationToken: ct);
        var (viaGrpc, _) = await ReadAsync(indicator);

        viaGrpc.Select(o => (o.SubjectKey, o.DocScope, o.Dimension))
            .Should().BeEquivalentTo(viaRest.Select(o => (o.SubjectKey, o.DocScope, o.Dimension)),
                "輸送を替えても保存される事実は変わらない");

        // 🔴 HTTP/1.1 のポートが生きていることの直接の観測（**readiness も 8080 のままである** ——
        // h2c にヘルスの面は足していない。[[IADR-0379]] 決定 3）。
        //
        // 🔴 **`/health/ready` ではなく `/health/live` で測る。** readiness は NpgSql の検査を
        // 含んでおり、器の DB は InMemory へ差し替えてあるので（到達不能な接続文字列が残る）
        // **ポートの生死とは無関係に赤になる**。ここで測りたいのは「8080 が消えていないこと」だけである。
        var live = await http.GetAsync("/health/live", ct);
        live.IsSuccessStatusCode.Should().BeTrue("h2c を有効にしても HTTP/1.1 側は残る");
    }

    // T-09: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-04 / T-05 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(KnowledgeHealthReportGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // s2s トークンの発行側を固定値へ差し替える（IdP を持たないため）。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }
}
