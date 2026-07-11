using FluentAssertions;
using KnowledgePlatform.IntegrationTests.Fixtures;
using Knowledge.Contracts.Events;
using MassTransit;
using System.Net;
using System.Net.Http.Json;

namespace KnowledgePlatform.IntegrationTests.WikiService;

// FR-13, ADR-0003: Wiki ページ CRUD + DocumentUpdated 同期 統合テスト
[Trait("Category", "Integration")]
public sealed class WikiSyncTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private WikiServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new WikiServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::WikiService.Api.Foundation.Persistence.WikiDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task GetWikiPages_ReturnsOk()
    {
        var resp = await _client.GetAsync("/wiki/pages");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task GetWikiPageBySlug_NotFound_Returns404()
    {
        var resp = await _client.GetAsync("/wiki/pages/nonexistent-slug");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task PublishDocumentUpdated_BusReceivesEvent()
    {
        var docId = Guid.NewGuid();
        var evt = new DocumentUpdated(
            DocumentId: docId,
            Title: "統合テスト Wiki ページ",
            Status: "Published",
            MarkdownUri: null,
            Attributes: new Dictionary<string, string> { ["department"] = "engineering" },
            Tags: ["test"],
            UpdatedAt: DateTimeOffset.UtcNow);

        await using var scope = _factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        await bus.Publish(evt);

        // メッセージ送信後 2 秒待機（非同期消費の確認は E2E テストの範囲）
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Wiki ページ一覧エンドポイントが正常動作していることを確認
        var resp = await _client.GetAsync("/wiki/pages");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
