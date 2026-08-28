using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.DataSourceService;

// FR-01, UC-04: DataSource 登録・同期トリガー 統合テスト
[Trait("Category", "Integration")]
public sealed class DataSourceTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private DataSourceServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new DataSourceServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::DataSourceService.Infrastructure.Persistence.DataSourceDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task CreateDataSource_ThenList_ContainsNew()
    {
        DockerRequired.SkipUnlessAvailable();
        var req = new
        {
            name = "社内 Confluence",
            sourceType = "Confluence",
            connectionUri = "https://confluence.example.com",
            config = new { spaceKey = "PROJ" }
        };

        var createResp = await _client.PostAsJsonAsync("/datasources", req, TestContext.Current.CancellationToken);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResp.Content.ReadFromJsonAsync<DataSourceResponse>(TestContext.Current.CancellationToken);
        created!.Name.Should().Be("社内 Confluence");
        created.Status.Should().BeOneOf("Active", "active");

        var listResp = await _client.GetAsync("/datasources", TestContext.Current.CancellationToken);
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResp.Content.ReadFromJsonAsync<List<DataSourceResponse>>(TestContext.Current.CancellationToken);
        list!.Should().Contain(d => d.Id == created.Id);
    }

    [Fact]
    public async Task TriggerSync_KnownDataSource_Returns202()
    {
        DockerRequired.SkipUnlessAvailable();
        var createResp = await _client.PostAsJsonAsync("/datasources", new
        {
            name = "Sync テスト",
            sourceType = "SharePoint",
            connectionUri = "https://sp.example.com",
            config = new { }
        }, TestContext.Current.CancellationToken);
        var ds = await createResp.Content.ReadFromJsonAsync<DataSourceResponse>(TestContext.Current.CancellationToken);

        var syncResp = await _client.PostAsync($"/datasources/{ds!.Id}/sync", null, TestContext.Current.CancellationToken);
        syncResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private record DataSourceResponse(Guid Id, string Name, string SourceType,
        string ConnectionUri, string Status);
}
