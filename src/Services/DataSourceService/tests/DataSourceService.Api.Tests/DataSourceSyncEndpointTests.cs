using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Api.Domain;
using DataSourceService.Api.Infrastructure;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
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

    // FR-05 回帰（IADR-0019）: 本対応マージ前から登録済みで DefaultAttributes が空 {} の既存データソースでも、
    // sync は confidentiality=internal を補完して発行し、fail-closed 除外（IADR-0012）を再発させない。
    [Fact]
    public async Task Sync_WithLegacyEmptyAttributes_PublishesRawDocumentWithDefaultConfidentiality()
    {
        var client = factory.CreateClient();
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        // マイグレーション前の「機密区分を持たない永続行」を再現する。Create のフェイルセーフを
        // EF のプロパティ上書きで回避し、DefaultAttributes を空 {} のまま保存する。
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
            var ds = DataSource.Create("legacy-share", "filesystem", "smb://legacy/docs");
            db.DataSources.Add(ds);
            db.Entry(ds).Property(nameof(DataSource.DefaultAttributes)).CurrentValue =
                new Dictionary<string, string>();
            await db.SaveChangesAsync();
            id = ds.Id;
        }

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null);
        res.EnsureSuccessStatusCode();

        var published = harness.Published.Select<RawDocumentFetched>()
            .Select(x => x.Context.Message)
            .First(m => m.SourceId == id);

        published.Attributes.Should().ContainKey("confidentiality")
            .WhoseValue.Should().Be("internal");
    }

    private static async Task<Guid> CreateDataSourceAsync(HttpClient client, object body)
    {
        var create = await client.PostAsJsonAsync("/datasources", body);
        create.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}
