using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-06, UC-03: DocumentService バージョン管理・メタデータ管理 統合テスト（実 PostgreSQL）
[Trait("Category", "Integration")]
public sealed class DocumentVersioningTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private DocumentServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new DocumentServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::DocumentService.Api.Foundation.Persistence.DocumentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task CreateUpdate_BuildsVersionHistory()
    {
        DockerRequired.SkipUnlessAvailable();
        // SC-05, #635: **タグは辞書に在る名前しか付けられない**（手入力は自動登録しない。
        // [[IADR-0153]] 決定 5）。辞書へ先に登録する。
        foreach (var name in new[] { "v1", "v2", "v3" })
            (await _client.PostAsJsonAsync("/tags", new { name }, TestContext.Current.CancellationToken)).StatusCode
                .Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);

        var create = await _client.PostAsJsonAsync("/documents", new
        {
            title = "版管理テスト",
            attributes = new { confidentiality = "internal", dept = "engineering" },
            tags = new[] { "v1" }
        }, TestContext.Current.CancellationToken);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<DocResponse>(TestContext.Current.CancellationToken);
        doc!.Version.Should().Be(1);

        // メタデータのみ更新（版 2）
        var patch = await _client.PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes = new { confidentiality = "internal", dept = "sales" },
            tags = new[] { "v2" }
        }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // タイトル更新（版 3）
        await _client.PutAsJsonAsync($"/documents/{doc.Id}",
            new { title = "改題", attributes = new { confidentiality = "internal" }, tags = new[] { "v3" } }, TestContext.Current.CancellationToken);

        var versionsResp = await _client.GetAsync($"/documents/{doc.Id}/versions", TestContext.Current.CancellationToken);
        versionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await versionsResp.Content.ReadFromJsonAsync<List<VersionResponse>>(TestContext.Current.CancellationToken);
        versions!.Count.Should().Be(3);
        versions[0].Version.Should().Be(3);

        // 版 1 のスナップショットが作成時点の属性・タイトルを保持する
        var v1Resp = await _client.GetAsync($"/documents/{doc.Id}/versions/1", TestContext.Current.CancellationToken);
        var v1 = await v1Resp.Content.ReadFromJsonAsync<VersionResponse>(TestContext.Current.CancellationToken);
        v1!.Title.Should().Be("版管理テスト");
        v1.Attributes["dept"].Should().Be("engineering");
    }

    [Fact]
    public async Task Update_WithStaleVersion_Returns409()
    {
        DockerRequired.SkipUnlessAvailable();
        var create = await _client.PostAsJsonAsync("/documents",
            new { title = "並行制御", attributes = new { confidentiality = "internal" }, tags = new string[] { } }, TestContext.Current.CancellationToken);
        var doc = await create.Content.ReadFromJsonAsync<DocResponse>(TestContext.Current.CancellationToken);

        var conflict = await _client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "x", attributes = new { confidentiality = "internal" }, tags = new string[] { }, expectedVersion = 99 }, TestContext.Current.CancellationToken);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private record DocResponse(Guid Id, string Title, string Status, int Version);

    private record VersionResponse(Guid DocumentId, int Version, string Title, string Status,
        Dictionary<string, string> Attributes, List<string> Tags, string? ChangeNote);
}
