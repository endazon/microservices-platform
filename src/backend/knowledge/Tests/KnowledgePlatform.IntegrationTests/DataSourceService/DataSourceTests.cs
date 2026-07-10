using FluentAssertions;
using KnowledgePlatform.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.IntegrationTests.DataSourceService;

// FR-01, UC-04: DataSource 登録・同期トリガー 統合テスト
[Trait("Category", "Integration")]
public sealed class DataSourceTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private DataSourceServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new DataSourceServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::DataSourceService.Api.Foundation.Persistence.DataSourceDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task CreateDataSource_ThenList_ContainsNew()
    {
        var req = new
        {
            name = "社内 Confluence",
            sourceType = "Confluence",
            connectionUri = "https://confluence.example.com",
            config = new { spaceKey = "PROJ" }
        };

        var createResp = await _client.PostAsJsonAsync("/datasources", req);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResp.Content.ReadFromJsonAsync<DataSourceResponse>();
        created!.Name.Should().Be("社内 Confluence");
        created.Status.Should().BeOneOf("Active", "active");

        var listResp = await _client.GetAsync("/datasources");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResp.Content.ReadFromJsonAsync<List<DataSourceResponse>>();
        list!.Should().Contain(d => d.Id == created.Id);
    }

    [DockerFact]
    public async Task TriggerSync_KnownDataSource_Returns202()
    {
        var createResp = await _client.PostAsJsonAsync("/datasources", new
        {
            name = "Sync テスト",
            sourceType = "SharePoint",
            connectionUri = "https://sp.example.com",
            config = new { }
        });
        var ds = await createResp.Content.ReadFromJsonAsync<DataSourceResponse>();

        var syncResp = await _client.PostAsync($"/datasources/{ds!.Id}/sync", null);
        syncResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private record DataSourceResponse(Guid Id, string Name, string SourceType,
        string ConnectionUri, string Status);
}
