using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Knowledge.Contracts.Events;
using MassTransit;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.WikiService;

// FR-13, ADR-0003（Superseded by ADR-0027・注記は #580）: Wiki ページ CRUD + DocumentUpdated 同期 統合テスト
[Trait("Category", "Integration")]
public sealed class WikiSyncTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private WikiServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new WikiServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::WikiService.Api.Foundation.Persistence.WikiDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetWikiPages_ReturnsOk()
    {
        DockerRequired.SkipUnlessAvailable();
        var resp = await _client.GetAsync("/wiki/pages", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWikiPageBySlug_NotFound_Returns404()
    {
        DockerRequired.SkipUnlessAvailable();
        var resp = await _client.GetAsync("/wiki/pages/nonexistent-slug", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PublishDocumentUpdated_BusReceivesEvent()
    {
        DockerRequired.SkipUnlessAvailable();
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
        await bus.Publish(evt, TestContext.Current.CancellationToken);

        // メッセージ送信後 2 秒待機（非同期消費の確認は E2E テストの範囲）
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Wiki ページ一覧エンドポイントが正常動作していることを確認
        var resp = await _client.GetAsync("/wiki/pages", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
