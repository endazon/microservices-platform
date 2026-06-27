using FluentAssertions;
using KnowledgePlatform.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.IntegrationTests.DocumentService;

// FR-06, UC-03: DocumentService バージョン管理・メタデータ管理 統合テスト（実 PostgreSQL）
[Trait("Category", "Integration")]
public sealed class DocumentVersioningTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private DocumentServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new DocumentServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::DocumentService.Api.Infrastructure.DocumentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task CreateUpdate_BuildsVersionHistory()
    {
        var create = await _client.PostAsJsonAsync("/documents", new
        {
            title = "版管理テスト",
            attributes = new { dept = "engineering" },
            tags = new[] { "v1" }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<DocResponse>();
        doc!.Version.Should().Be(1);

        // メタデータのみ更新（版 2）
        var patch = await _client.PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes = new { dept = "sales" },
            tags = new[] { "v2" }
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // タイトル更新（版 3）
        await _client.PutAsJsonAsync($"/documents/{doc.Id}",
            new { title = "改題", tags = new[] { "v3" } });

        var versionsResp = await _client.GetAsync($"/documents/{doc.Id}/versions");
        versionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await versionsResp.Content.ReadFromJsonAsync<List<VersionResponse>>();
        versions!.Count.Should().Be(3);
        versions[0].Version.Should().Be(3);

        // 版 1 のスナップショットが作成時点の属性・タイトルを保持する
        var v1Resp = await _client.GetAsync($"/documents/{doc.Id}/versions/1");
        var v1 = await v1Resp.Content.ReadFromJsonAsync<VersionResponse>();
        v1!.Title.Should().Be("版管理テスト");
        v1.Attributes["dept"].Should().Be("engineering");
    }

    [DockerFact]
    public async Task Update_WithStaleVersion_Returns409()
    {
        var create = await _client.PostAsJsonAsync("/documents",
            new { title = "並行制御", tags = new string[] { } });
        var doc = await create.Content.ReadFromJsonAsync<DocResponse>();

        var conflict = await _client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "x", tags = new string[] { }, expectedVersion = 99 });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private record DocResponse(Guid Id, string Title, string Status, int Version);

    private record VersionResponse(Guid DocumentId, int Version, string Title, string Status,
        Dictionary<string, string> Attributes, List<string> Tags, string? ChangeNote);
}
