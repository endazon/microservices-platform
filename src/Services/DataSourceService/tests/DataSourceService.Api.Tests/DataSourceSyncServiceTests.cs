using DataSourceService.Api.Composable.Adapters;
using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Ports;
using DataSourceService.Api.Foundation.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Ports.Storage;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Api.Tests;

// FR-01, UC-04, IADR-0051, claude-review #220（🔴）: 増分 watermark（LastSyncedAt）は完全成功時のみ前進し、
// discover 失敗・一部 fetch 失敗時は前進しないこと（＝失敗ファイルが次回再試行対象に残る）を検証する。
public sealed class DataSourceSyncServiceTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task Sync_DiscoverFailure_DoesNotAdvanceWatermark_AndTracksFailure()
    {
        using var scope = factory.Services.CreateScope();
        var tracker = new SyncFailureTracker();
        var svc = BuildService(scope, new DiscoverThrowingConnector(), tracker);
        var source = DataSource.Create("boom", "filesystem", "");
        source.LastSyncedAt.Should().BeNull();

        var result = await svc.SyncAsync(source);

        result.ConnectorAvailable.Should().BeTrue();
        result.DiscoverSucceeded.Should().BeFalse();
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("discover 失敗時は watermark を進めず次回再試行できるようにする");
        tracker.Current(source.Id).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Sync_PartialFetchFailure_DoesNotAdvanceWatermark()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new FetchThrowingConnector(), new SyncFailureTracker());
        var source = DataSource.Create("partial", "filesystem", "");

        var result = await svc.SyncAsync(source);

        result.Failed.Should().Be(1);
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("一部取得失敗時も watermark を進めない（失敗分を次回再試行）");
    }

    [Fact]
    public async Task Sync_FullSuccess_AdvancesWatermark()
    {
        using var scope = factory.Services.CreateScope();
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var svc = BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance),
            new SyncFailureTracker());
        var source = DataSource.Create("share", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        var result = await svc.SyncAsync(source);

        result.Fetched.Should().Be(1);
        result.Failed.Should().Be(0);
        result.ShouldAdvanceWatermark.Should().BeTrue();
        source.LastSyncedAt.Should().NotBeNull("完全成功時は watermark を前進させる");
    }

    private static DataSourceSyncService BuildService(
        IServiceScope scope, IDataSourceConnector connector, SyncFailureTracker tracker)
    {
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var registry = new ConnectorRegistry([connector]);
        return new DataSourceSyncService(registry, storage, bus, tracker,
            NullLogger<DataSourceSyncService>.Instance);
    }

    private string CreateTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        _tempDirs.Add(dir);
        return dir;
    }

    // discover が失敗するコネクタ（一時的な接続断・権限エラーを模す）。
    private sealed class DiscoverThrowingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => throw new IOException("discover boom");
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new NotSupportedException();
    }

    // discover は 1 件返すが fetch で失敗するコネクタ（一部取得失敗を模す）。
    private sealed class FetchThrowingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceItem>>([new SourceItem("/x/a.md", DateTimeOffset.UtcNow, 1)]);
        public Task<RawContent> FetchAsync(DataSource s, SourceItem item, CancellationToken ct)
            => throw new IOException("fetch boom");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
