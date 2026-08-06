using FluentAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-01, FR-02, UC-04, SC-06, IADR-0039: /bff/datasources が DataSourceService へ集約し、
// 管理者・運用者ロールに限定されること（権限外は 403・無認証は 401）、CRUD・同期を中継することを検証する。
public class BffDataSourceEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDataSourceEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.DataSourceStatusCode = HttpStatusCode.OK;
    }

    [Fact]
    public async Task GetList_AsAdmin_ReturnsDataSources()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DataSourceDto>>();
        body!.Should().HaveCount(2);
        body[0].Name.Should().Be("社内共有フォルダ");
    }

    // SC-06（planning#200 / 裁定 Q15）, IADR-0136: 次回同期は後段が計算する共通間隔の次回実行時刻であり、
    // BFF は**全ソース同値のまま透過**する。BFF は DataSourceDto で型付けして中継するだけなので実装は
    // 変わらないが、契約のメンバーが増えたときに落ちる場所が無いと静かに欠落する（ここで固定する）。
    [Fact]
    public async Task GetList_PassesThroughNextSyncAt()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DataSourceDto>>();
        body!.Should().OnlyContain(d => d.NextSyncAt == BffTestFactory.StubNextSyncAt,
            "後段が返す次回同期を欠落させず、ソースごとに変えもしない");
    }

    [Fact]
    public async Task GetList_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetList_AsNonPrivilegedRole_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        var resp = await client.GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetList_WhenAnonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        var resp = await client.GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_WhenMissing_Returns404()
    {
        _factory.DataSourceStatusCode = HttpStatusCode.NotFound;
        var resp = await _factory.CreateClient().GetAsync($"/bff/datasources/{BffTestFactory.StubDataSourceId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetList_WhenBackendFails_SurfacesFailure_NotEmptyList()
    {
        // 管理画面では後段障害を空一覧へ縮退させない（「未登録」との誤認・重複登録を防ぐ・レビュー #169 指摘対応）。
        _factory.DataSourceStatusCode = HttpStatusCode.ServiceUnavailable;
        var resp = await _factory.CreateClient().GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/bff/datasources",
            new CreateDataSourceRequest("新ソース", "filesystem", "smb://x/y"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<DataSourceDto>();
        body!.Name.Should().Be("社内共有フォルダ"); // スタブは固定応答を返す
    }

    [Fact]
    public async Task Sync_AsAdmin_Returns202()
    {
        var resp = await _factory.CreateClient()
            .PostAsync($"/bff/datasources/{BffTestFactory.StubDataSourceId}/sync", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Delete_AsAdmin_Returns204()
    {
        var resp = await _factory.CreateClient()
            .DeleteAsync($"/bff/datasources/{BffTestFactory.StubDataSourceId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
