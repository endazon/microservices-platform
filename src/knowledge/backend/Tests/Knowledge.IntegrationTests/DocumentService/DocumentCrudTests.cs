using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-06, UC-03: DocumentService CRUD 統合テスト（実 PostgreSQL）
[Trait("Category", "Integration")]
public sealed class DocumentCrudTests(PostgresFixture postgres, RabbitMqFixture rabbit)
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
        var db = scope.ServiceProvider.GetRequiredService<global::DocumentService.Api.Foundation.Persistence.DocumentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    // SC-05, #635: **タグは辞書に在る名前しか付けられない**（手入力は自動登録しない。
    // [[IADR-0153]] 決定 5）。辞書へ先に登録する。
    private async Task RegisterTagsAsync(params string[] names)
    {
        foreach (var name in names)
            (await _client.PostAsJsonAsync("/tags", new { name })).StatusCode
                .Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task CreateDocument_ThenGet_ReturnsDocument()
    {
        // Arrange
        await RegisterTagsAsync("test", "integration");
        var req = new
        {
            title = "統合テスト文書",
            originalUri = "https://example.com/doc.pdf",
            contentType = "application/pdf",
            attributes = new { confidentiality = "internal", department = "engineering" },
            tags = new[] { "test", "integration" }
        };

        // Act — POST /documents
        var createResp = await _client.PostAsJsonAsync("/documents", req);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResp.Content.ReadFromJsonAsync<DocumentResponse>();
        created.Should().NotBeNull();
        created!.Title.Should().Be("統合テスト文書");

        // Act — GET /documents/{id}
        var getResp = await _client.GetAsync($"/documents/{created.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await getResp.Content.ReadFromJsonAsync<DocumentResponse>();
        fetched!.Title.Should().Be("統合テスト文書");
        fetched.Status.Should().BeOneOf("Draft", "draft");
    }

    [DockerFact]
    public async Task ListDocuments_ReturnsAll()
    {
        await _client.PostAsJsonAsync("/documents", new { title = "文書 A", attributes = new { confidentiality = "internal" }, tags = new string[] { } });
        await _client.PostAsJsonAsync("/documents", new { title = "文書 B", attributes = new { confidentiality = "internal" }, tags = new string[] { } });

        var resp = await _client.GetAsync("/documents");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await resp.Content.ReadFromJsonAsync<List<DocumentResponse>>();
        list.Should().NotBeNull();
        // ［2026-08-21 / #455 A-3］BeGreaterOrEqualTo は FluentAssertions v7 の非推奨エイリアスで、
        // AwesomeAssertions（v7 系フォーク）には**存在しない**。新名 BeGreaterThanOrEqualTo を使う。
        list!.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [DockerFact]
    public async Task UpdateDocument_ChangesTitle()
    {
        var createResp = await _client.PostAsJsonAsync("/documents",
            new { title = "元タイトル", attributes = new { confidentiality = "internal" }, tags = new string[] { } });
        var doc = await createResp.Content.ReadFromJsonAsync<DocumentResponse>();

        var updateResp = await _client.PutAsJsonAsync($"/documents/{doc!.Id}",
            new { title = "新しいタイトル", attributes = new { confidentiality = "internal" }, tags = new string[] { } });
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResp = await _client.GetAsync($"/documents/{doc.Id}");
        var updated = await getResp.Content.ReadFromJsonAsync<DocumentResponse>();
        updated!.Title.Should().Be("新しいタイトル");
    }

    private record DocumentResponse(Guid Id, string Title, string Status, string? MarkdownUri,
        Dictionary<string, string> Attributes, List<string> Tags);
}
