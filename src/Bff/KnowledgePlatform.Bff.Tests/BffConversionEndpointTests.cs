using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Tests;

// FR-12, UC-06, SC-07, IADR-0042: /bff/conversion/jobs が ConversionService へ集約し、管理者・運用者に
// 限定されること（権限外 403・無認証 401）、状況一覧（絞り込み）・個別取得・人手補正（再変換）を中継することを検証する。
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

    [Fact]
    public async Task Retry_WhenJobUnknown_Passes404Through()
    {
        _factory.ConversionStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/conversion/jobs/{Guid.NewGuid()}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
