using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-12, UC-06, SC-07, IADR-0042: /bff/conversion/jobs が ConversionService へ集約し、管理者・運用者に
// 限定されること（権限外 403・無認証 401）、状況一覧（絞り込み）・個別取得・人手補正（再変換）を中継することを検証する。
// FR-12, SC-07, IADR-0128（2026-08-04 確定）: **再変換（retry）だけは管理者ロール限定**であり、照会（GET）は
// 据え置き（admin または operator）であること——境界の両側——を固定する。
public class BffConversionEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffConversionEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.ConversionStatusCode = HttpStatusCode.OK;
        _factory.ConversionThrows = false;
        // IADR-0154: 本文つき 409 と観測用パスも毎テスト戻す（IClassFixture は共有されるため、
        // 戻し忘れると後続テストの retry 応答へ本文が漏れる）。
        _factory.ConversionConflictBody = null;
        _factory.LastConversionPath = null;
    }

    [Fact]
    public async Task GetList_AsAdmin_ReturnsJobs()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<ConversionJobDto>>();
        body!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetList_FiltersByStatus()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/conversion/jobs?status=failed");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<ConversionJobDto>>();
        body!.Should().ContainSingle(j => j.Status == ConversionJobStatus.Failed);
    }

    [Fact]
    public async Task GetList_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetList_AsNonPrivilegedRole_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        var resp = await client.GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetList_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // FR-12, SC-07, IADR-0128 決定2: 個別取得の権限は据え置き（運用者も可）。retry を絞った際に
    // グループごと巻き添えで絞られていないことを、照会の側から固定する（回帰）。
    [Fact]
    public async Task GetById_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-12, SC-07, IADR-0039 決定3: 無認証は 401（権限不足の 403 と取り違えない）。一覧側には
    // GetList_WhenAnonymous_IsUnauthorized があるのに個別取得には無く、非対称が残っていた。
    [Fact]
    public async Task GetById_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.GetAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_WhenMissing_Returns404()
    {
        _factory.ConversionStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient().GetAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetList_WhenBackendFails_SurfacesFailure_NotEmptyList()
    {
        // 運用画面では後段障害を空一覧へ縮退させない（「ジョブ無し」と障害を区別・レビュー #172 指摘対応）。
        _factory.ConversionStatusCode = HttpStatusCode.ServiceUnavailable;
        var resp = await _factory.CreateClient().GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetList_WhenBackendUnreachable_Returns502()
    {
        // 後段不達（HttpRequestException）は 502 へ縮退する（catch 分岐の直接検証・レビュー #172 指摘対応）。
        _factory.ConversionThrows = true;
        var resp = await _factory.CreateClient().GetAsync("/bff/conversion/jobs");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Retry_AsAdmin_Returns202()
    {
        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // FR-12, SC-07, IADR-0128 決定1（計画 05_screens §SC-07 2026-08-04 確定）:
    // 再変換の実行権限は管理者ロールに限る。**照会は許される運用者でも retry は 403** になる。
    // これが本 issue（#501）の核心——「admin で通ること」だけを見るテストでは、誰でも通る状態を
    // 検出できない。GetList_AsOperator_IsAllowed と対で、ロールの境界を両側から固定する。
    [Fact]
    public async Task Retry_AsOperator_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-12, SC-07, IADR-0128 決定1: 無認証は 401（認証の欠如と権限不足を取り違えない）。
    [Fact]
    public async Task Retry_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Retry_WhenJobUnknown_Passes404Through()
    {
        _factory.ConversionStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{Guid.NewGuid()}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-12, UC-06, SC-07: 失敗以外（processing 等）の再変換は後段が 409 not_retryable を返す。
    // BFF はそれを素通しする（認可を絞っても後段の状態強制が変わっていないことの回帰）。
    // 409 の本文（error=not_retryable）そのものは後段の ConversionJobEndpointTests が検証する。
    [Fact]
    public async Task Retry_WhenNotRetryable_Passes409Through()
    {
        _factory.ConversionStatusCode = HttpStatusCode.Conflict;
        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1 の 3 本。
    // **計画 05_screens:314「再変換の実行と人手補正は管理者限定」** に従い、照会（GET /figures）も
    // 管理者限定にする——2 ペインを開く操作そのものが人手補正であるため。
    [Fact]
    public async Task Figures_AsAdmin_IsAllowed()
    {
        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/figures");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var figures = await resp.Content.ReadFromJsonAsync<List<ConversionFigureDto>>();
        figures!.Should().HaveCount(2);
        figures.Should().ContainSingle(f => !f.Coded).Which.ImageUri.Should().NotBeNull();
    }

    // **運用者は照会画面（一覧・個別）は見られるが、人手補正には入れない。**
    // 「admin で通ること」だけを見るテストでは、誰でも通る状態を検出できない（#501 の教訓）。
    [Theory]
    [InlineData("figures")]
    [InlineData("figures/fig-1/image")]
    public async Task FigureReads_AsOperator_AreForbidden(string suffix)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/{suffix}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Correction_AsAdmin_IsAllowed()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync(
            $"/bff/conversion/jobs/{BffTestFactory.StubJobId}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Correction_AsOperator_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.PostAsJsonAsync(
            $"/bff/conversion/jobs/{BffTestFactory.StubJobId}/figures/fig-1/correction",
            new FigureCorrectionRequest("mermaid", "flowchart LR; X-->Y;"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ★ IADR-0154 決定 4 / #640 と同型の回帰: **409 の本文を落とさない**。
    // 従前この BFF は Results.StatusCode で状態コードだけを返しており、後段が載せた理由・件数が
    // 画面まで届かなかった。corrections_would_be_lost は件数を出して確認を求めるための応答なので、
    // 本文が消えると画面が「何件失われるか」を言えない。
    [Fact]
    public async Task Retry_Passes409Body_ThroughVerbatim()
    {
        _factory.ConversionStatusCode = HttpStatusCode.Conflict;
        _factory.ConversionConflictBody =
            """{"error":"corrections_would_be_lost","status":"failed","correctedFigures":2}""";

        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("corrections_would_be_lost");
        body.Should().Contain("\"correctedFigures\":2");
    }

    // 明示確認は後段へ伝わること（BFF が握り潰すと補正は永遠に破棄できない）。
    [Fact]
    public async Task Retry_ForwardsDiscardConfirmation_ToDownstream()
    {
        await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{BffTestFactory.StubJobId}/retry?discardCorrections=true",
                content: null);

        _factory.LastConversionPath.Should().Contain("discardCorrections=true");
    }
}
