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

    // SC-06（裁定 Q14 / #537）: 同期健全性も NextSyncAt と同じく**欠落させずに透過**する。
    // BFF は DataSourceDto で型付けして中継するだけなので実装は変わらないが、契約のメンバーが
    // 増えたときに落ちる場所が無いと静かに欠落する（ここで固定する）。
    [Fact]
    public async Task GetList_PassesThroughSyncHealth()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<DataSourceDto>>();
        body!.Should().OnlyContain(d => d.RetryLimit == BffTestFactory.StubRetryLimit,
            "しきい値の分母は後段が返す値をそのまま運ぶ（BFF で定数を持たない）");
        var failing = body.Single(d => d.ConsecutiveFailureCount >= d.RetryLimit);
        failing.LastSyncError.Should().Be("connect failed: Host=db;Password=***",
            "直近エラーは後段でマスク済みのまま運ぶ（BFF は再マスクも復元もしない）");
        failing.LastSyncErrorAt.Should().NotBeNull();
    }

    // SC-06（裁定 Q16 / #534）: 更新（PUT 全置換）を中継する。
    [Fact]
    public async Task Put_AsAdmin_ForwardsAndReturnsUpdated()
    {
        var resp = await _factory.CreateClient().PutAsJsonAsync(
            $"/bff/datasources/{BffTestFactory.StubDataSourceId}",
            new UpdateDataSourceRequest("後", "wiki", "https://wiki.example.test"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<DataSourceDto>()).Should().NotBeNull();
        _factory.LastDataSourceUpdateMethod.Should().Be(HttpMethod.Put);
    }

    // PATCH は「省略＝現状維持」が意味を持つ。**BFF が省略項目を埋めて転送してはならない**
    // （埋めると後段には「明示的に null で上書きせよ」と届き、部分更新の意味が消える）。
    [Fact]
    public async Task Patch_AsAdmin_ForwardsOnlyProvidedFields()
    {
        var resp = await _factory.CreateClient().PatchAsJsonAsync(
            $"/bff/datasources/{BffTestFactory.StubDataSourceId}",
            new PatchDataSourceRequest(ConnectionUri: "smb://relocated/share"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastDataSourceUpdateMethod.Should().Be(HttpMethod.Patch);
        _factory.LastDataSourceUpdateBody.Should().Contain("smb://relocated/share");
    }

    // 計画 §SC-06「登録・更新・無効化は管理者限定」。**運用者は閲覧できるが更新はできない。**
    // グループ既定（admin ＋ operator）に AdminOnly を積んで実効させている（IADR-0128 と同じ形）。
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Update_AsOperator_IsForbidden(string method)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        using var req = new HttpRequestMessage(
            new HttpMethod(method), $"/bff/datasources/{BffTestFactory.StubDataSourceId}")
        {
            Content = JsonContent.Create(new UpdateDataSourceRequest("x", "filesystem", "smb://x")),
        };

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-01, SC-06（#628）: **登録・無効化も管理者限定**である（計画 §SC-06・裁定 Q19）。
    // 従前はグループ既定（admin ＋ operator）のままで計画より広かった。**後段と同時に狭める**
    // （[[IADR-0044]] の多層防御。片側だけだと BFF 迂回で通る／画面だけ 403 になる）。
    [Fact]
    public async Task Create_AsOperator_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.PostAsJsonAsync("/bff/datasources",
            new CreateDataSourceRequest("新ソース", "filesystem", "smb://x/y"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_AsOperator_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.DeleteAsync($"/bff/datasources/{BffTestFactory.StubDataSourceId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // FR-01, UC-04, SC-06（#628 / planning#299・2026-08-09）: **手動同期は破壊的操作に含めない。**
    // 運用者に開いたままであることを対で固定する——登録・無効化を狭めた勢いで一緒に絞ると、
    // 「運用者が異常に気づいたその場で再同期して一次対応する」という計画の裁定を壊す。
    [Fact]
    public async Task Sync_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.PostAsync(
            $"/bff/datasources/{BffTestFactory.StubDataSourceId}/sync", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task GetList_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync("/bff/datasources");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-01, SC-06（#628）: 個別取得も運用者に開いたままである（**閲覧を狭めない**）。
    [Fact]
    public async Task GetById_AsOperator_IsAllowed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        var resp = await client.GetAsync($"/bff/datasources/{BffTestFactory.StubDataSourceId}");

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
