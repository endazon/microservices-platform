using FluentAssertions;
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
}
