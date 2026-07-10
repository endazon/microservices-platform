using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace DocumentService.Api.Tests;

public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // FR-06: ヘルスエンドポイント
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-06, UC-03: 文書一覧エンドポイント
    [Fact]
    public async Task GetDocuments_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/documents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var docs = await response.Content.ReadFromJsonAsync<List<DocumentDto>>();
        docs.Should().NotBeNull();
    }

    // FR-06, UC-03: 文書登録エンドポイント
    [Fact]
    public async Task PostDocument_Returns201()
    {
        var req = new { Title = "テスト文書", OriginalUri = (string?)null, ContentType = (string?)null, Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" }, Tags = new List<string>() };
        var response = await factory.CreateClient().PostAsJsonAsync("/documents", req);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
