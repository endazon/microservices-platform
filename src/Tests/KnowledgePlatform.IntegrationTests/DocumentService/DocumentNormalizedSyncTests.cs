using FluentAssertions;
using KnowledgePlatform.IntegrationTests.Fixtures;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.IntegrationTests.DocumentService;

// FR-01, UC-04: DocumentNormalized 購読 → カタログ登録 統合テスト（実 PostgreSQL / RabbitMQ）
[Trait("Category", "Integration")]
public sealed class DocumentNormalizedSyncTests(PostgresFixture postgres, RabbitMqFixture rabbit)
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

    // FR-01: DocumentNormalized を発行するとカタログへ status=normalized で登録される
    [DockerFact]
    public async Task PublishDocumentNormalized_CatalogsDocument()
    {
        var docId = Guid.NewGuid();
        var evt = new DocumentNormalized(
            DocumentId: docId,
            SourceId: Guid.NewGuid(),
            Title: "正規化テスト文書",
            MarkdownUri: "storage://normalized/doc.md",
            AssetUris: [],
            Attributes: new Dictionary<string, string> { ["department"] = "engineering" },
            Tags: ["fr-01", "integration"],
            NormalizedAt: DateTimeOffset.UtcNow);

        await PublishAsync(evt);

        var doc = await WaitForDocumentAsync(docId);
        doc.Should().NotBeNull("DocumentNormalized 受信後にカタログ登録されるはず");
        doc!.Title.Should().Be("正規化テスト文書");
        doc.Status.Should().Be("normalized");
        doc.MarkdownUri.Should().Be("storage://normalized/doc.md");
    }

    // FR-01: 同一 DocumentId の再配信でも重複登録されない（冪等）
    [DockerFact]
    public async Task PublishDocumentNormalized_Twice_IsIdempotent()
    {
        var docId = Guid.NewGuid();
        DocumentNormalized Make(string title) => new(
            DocumentId: docId,
            SourceId: Guid.NewGuid(),
            Title: title,
            MarkdownUri: "storage://normalized/doc.md",
            AssetUris: [],
            Attributes: new Dictionary<string, string>(),
            Tags: [],
            NormalizedAt: DateTimeOffset.UtcNow);

        await PublishAsync(Make("初回タイトル"));
        await WaitForDocumentAsync(docId);
        await PublishAsync(Make("更新タイトル"));

        // 再配信後、件数が増えず（同一 ID）タイトルが更新されること
        var updated = await WaitForDocumentAsync(docId, expectedTitle: "更新タイトル");
        updated.Should().NotBeNull();
        updated!.Title.Should().Be("更新タイトル");

        var listResp = await _client.GetAsync("/documents");
        var list = await listResp.Content.ReadFromJsonAsync<List<DocumentResponse>>();
        list!.Count(d => d.Id == docId).Should().Be(1, "同一 DocumentId は 1 件のみ");
    }

    private async Task PublishAsync(DocumentNormalized evt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        await bus.Publish(evt);
    }

    // 非同期消費を最大 ~30 秒ポーリングして待つ（Issue #33: CI のタイミング揺らぎに対する暫定対処）
    private async Task<DocumentResponse?> WaitForDocumentAsync(Guid id, string? expectedTitle = null)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var resp = await _client.GetAsync($"/documents/{id}");
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var doc = await resp.Content.ReadFromJsonAsync<DocumentResponse>();
                if (doc is not null && (expectedTitle is null || doc.Title == expectedTitle))
                    return doc;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        return null;
    }

    private record DocumentResponse(Guid Id, string Title, string Status, string? MarkdownUri,
        Dictionary<string, string> Attributes, List<string> Tags);
}
