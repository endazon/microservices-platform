using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-01, FR-05, UC-04, IADR-0051: 同期トリガーが実 filesystem コネクタ経由で原本を取得し、
// 既定 ABAC 属性（機密区分）を付与した RawDocumentFetched を発行することを検証する。
// 一時ディレクトリに実ファイルを置き、コネクタがそれを列挙・取得する（オブジェクトストレージは
// 未構成のため NullObjectStorageClient で縮退＝決定的 URI 発行）。
public class DataSourceSyncEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly List<string> _tempDirs = [];

    // FR-05: 機密区分未指定で登録した filesystem データソースの sync は confidentiality=internal を付与して発行する。
    [Fact]
    public async Task Sync_WithoutExplicitAttributes_PublishesRawDocumentWithDefaultConfidentiality()
    {
        var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var dir = CreateTempDirWithFile("guide.md", "# Guide");

        var id = await CreateDataSourceAsync(client, new
        {
            name = "fileserver",
            sourceType = "filesystem",
            connectionUri = "file://share/docs",
            config = new Dictionary<string, string> { ["rootPath"] = dir },
        });

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();

        var published = FirstPublishedFor(bus, id);
        published.Attributes.Should().ContainKey("confidentiality").WhoseValue.Should().Be("internal");
        published.OriginalPath.Should().EndWith("guide.md");
        published.ContentType.Should().Be("text/markdown");
    }

    // FR-05: 明示した機密区分・部門属性がそのまま原本イベントへ付与される。
    [Fact]
    public async Task Sync_WithExplicitAttributes_PropagatesThemToRawDocument()
    {
        var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var dir = CreateTempDirWithFile("policy.txt", "hr policy");

        var id = await CreateDataSourceAsync(client, new
        {
            name = "hr-share",
            sourceType = "filesystem",
            connectionUri = "",
            config = new Dictionary<string, string> { ["rootPath"] = dir },
            defaultAttributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "confidential",
                ["department"] = "hr",
            },
        });

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();

        var published = FirstPublishedFor(bus, id);
        published.Attributes["confidentiality"].Should().Be("confidential");
        published.Attributes["department"].Should().Be("hr");
    }

    // FR-05 回帰（IADR-0019）: DefaultAttributes が空 {} の既存データソースでも sync は
    // confidentiality=internal を補完して発行し、fail-closed 除外（IADR-0012）を再発させない。
    [Fact]
    public async Task Sync_WithLegacyEmptyAttributes_PublishesRawDocumentWithDefaultConfidentiality()
    {
        var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();
        var dir = CreateTempDirWithFile("legacy.md", "legacy");

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
            var ds = DataSource.Create("legacy-share", "filesystem", "",
                new Dictionary<string, string> { ["rootPath"] = dir });
            // マイグレーション前の「機密区分を持たない永続行」を再現（Create のフェイルセーフを EF 上書きで回避）。
            db.DataSources.Add(ds);
            db.Entry(ds).Property(nameof(DataSource.DefaultAttributes)).CurrentValue =
                new Dictionary<string, string>();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = ds.Id;
        }

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();

        var published = FirstPublishedFor(bus, id);
        published.Attributes.Should().ContainKey("confidentiality").WhoseValue.Should().Be("internal");
    }

    // IADR-0051: 未対応 SourceType はコネクタ未実装のため縮退する（5xx にせず、発行 0 件）。
    // filesystem(#195)/wiki(#217)/saas(#218)/db(#219) は実装済みのため、恒久的に未対応の架空種別
    // "unknown-source" を用いる（将来コネクタが増えても本テストが壊れないようにする）。
    [Fact]
    public async Task Sync_UnsupportedSourceType_DegradesWithoutPublishing()
    {
        var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<RecordingMessageBus>();

        var id = await CreateDataSourceAsync(client, new
        {
            name = "unknown-src",
            sourceType = "unknown-source",
            connectionUri = "https://example.com",
        });

        var res = await client.PostAsync($"/datasources/{id}/sync", content: null, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("connectorAvailable").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("fetched").GetInt32().Should().Be(0);
        bus.PublishedOf<RawDocumentFetched>().Where(m => m.SourceId == id)
            .Should().BeEmpty("コネクタが無いときは 1 件も発行しない");
    }

    private static RawDocumentFetched FirstPublishedFor(RecordingMessageBus bus, Guid id)
    {
        var matches = bus.PublishedOf<RawDocumentFetched>().Where(m => m.SourceId == id).ToList();
        matches.Should().NotBeEmpty("同期が成功したなら当該ソースの原本イベントが発行される");
        return matches[0];
    }

    private string CreateTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kp-ds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        _tempDirs.Add(dir);
        return dir;
    }

    private static async Task<Guid> CreateDataSourceAsync(HttpClient client, object body)
    {
        var create = await client.PostAsJsonAsync("/datasources", body, TestContext.Current.CancellationToken);
        create.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
