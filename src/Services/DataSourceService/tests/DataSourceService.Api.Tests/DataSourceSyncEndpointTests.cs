using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Api.Tests;

// FR-01, FR-05, UC-04: 同期トリガーが原本イベントへ既定 ABAC 属性（機密区分）を付与することを検証
public class DataSourceSyncEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // FR-05: 機密区分未指定で登録したデータソースの sync は confidentiality=internal を付与して発行する
    [Fact]
    public async Task Sync_WithoutExplicitAttributes_PublishesRawDocumentWithDefaultConfidentiality()
    {
        var client = factory.CreateClient();
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        var id = await CreateDataSourceAsync(client, new
        {
            name = "fileserver",
            sourceType = "filesystem",
            connectionUri = "smb://share/docs",
        });

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null);
        res.EnsureSuccessStatusCode();

        (await harness.Published.Any<RawDocumentFetched>()).Should().BeTrue();
        var published = harness.Published.Select<RawDocumentFetched>()
            .Select(x => x.Context.Message)
            .First(m => m.SourceId == id);

        published.Attributes.Should().ContainKey("confidentiality")
            .WhoseValue.Should().Be("internal");
    }

    // FR-05: 明示した機密区分・部門属性がそのまま原本イベントへ付与される
    [Fact]
    public async Task Sync_WithExplicitAttributes_PropagatesThemToRawDocument()
    {
        var client = factory.CreateClient();
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        var id = await CreateDataSourceAsync(client, new
        {
            name = "hr-wiki",
            sourceType = "wiki",
            connectionUri = "https://wiki/hr",
            defaultAttributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "confidential",
                ["department"] = "hr",
            },
        });

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null);
        res.EnsureSuccessStatusCode();

        var published = harness.Published.Select<RawDocumentFetched>()
            .Select(x => x.Context.Message)
            .First(m => m.SourceId == id);

        published.Attributes["confidentiality"].Should().Be("confidential");
        published.Attributes["department"].Should().Be("hr");
    }

    private static async Task<Guid> CreateDataSourceAsync(HttpClient client, object body)
    {
        var create = await client.PostAsJsonAsync("/datasources", body);
        create.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}
