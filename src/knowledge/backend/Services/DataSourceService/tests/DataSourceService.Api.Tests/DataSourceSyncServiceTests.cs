using DataSourceService.Api.Composable.Adapters;
using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Ports;
using DataSourceService.Api.Foundation.Services;
using FluentAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
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
        var svc = BuildService(scope, new DiscoverThrowingConnector());
        var source = DataSource.Create("boom", "filesystem", "");
        source.LastSyncedAt.Should().BeNull();

        var result = await svc.SyncAsync(source);

        result.ConnectorAvailable.Should().BeTrue();
        result.DiscoverSucceeded.Should().BeFalse();
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("discover 失敗時は watermark を進めず次回再試行できるようにする");
        // SC-06（Q14 / #537）: 計数はエンティティに載る（永続化され SC-06 が読む）。
        source.ConsecutiveFailureCount.Should().Be(1);
        source.LastSyncError.Should().NotBeNull();
        source.LastSyncErrorAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sync_PartialFetchFailure_DoesNotAdvanceWatermark()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new FetchThrowingConnector());
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
        var svc = BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance));
        var source = DataSource.Create("share", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        var result = await svc.SyncAsync(source);

        result.Fetched.Should().Be(1);
        result.Failed.Should().Be(0);
        result.ShouldAdvanceWatermark.Should().BeTrue();
        source.LastSyncedAt.Should().NotBeNull("完全成功時は watermark を前進させる");
    }

    // FR-01, UC-04 例外フロー, SC-06（Q14 / #537）: 継続失敗のしきい値は**再試行上限に達した時点**である
    // （計画 §SC-06。hi-fi の「3/5」は「5 回中 3 回目」という進捗表示であって、しきい値 3 の意味ではない）。
    // 従前の実装は独自に 3 を持っており、計画が「実装が決めることになる」として排した状態だった。
    [Fact]
    public async Task Sync_RepeatedFailures_ReachAlertThresholdAtRetryLimit()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new DiscoverThrowingConnector());
        var source = DataSource.Create("flaky", "filesystem", "");

        // しきい値の 1 つ手前までは未到達である。
        for (var i = 1; i < DataSourceSyncService.AlertThreshold; i++)
        {
            await svc.SyncAsync(source);
            source.ConsecutiveFailureCount.Should().Be(i);
            source.ConsecutiveFailureCount.Should().BeLessThan(DataSourceSyncService.AlertThreshold);
        }

        await svc.SyncAsync(source);

        source.ConsecutiveFailureCount.Should().Be(DataSourceSyncService.AlertThreshold);
        DataSourceSyncService.AlertThreshold.Should().Be(DataSourceSyncHealth.DefaultRetryLimit,
            "しきい値と再試行上限は同一の定数である（2 つ持つと片方が黙って古くなる）");
    }

    // SC-06（Q14 / #537）: 完全成功で健全性が初期状態へ戻る。**直近エラーも消す** ——
    // 残すと「正常なのに ⚠ の材料が残っている」状態になり、画面の判断が割れる。
    [Fact]
    public async Task Sync_SuccessAfterFailures_ClearsHealth()
    {
        using var scope = factory.Services.CreateScope();
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var source = DataSource.Create("recovering", "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = dir });

        await BuildService(scope, new DiscoverThrowingConnector()).SyncAsync(source);
        source.ConsecutiveFailureCount.Should().Be(1);
        source.LastSyncError.Should().NotBeNull();

        await BuildService(scope, new FileSystemConnector(NullLogger<FileSystemConnector>.Instance))
            .SyncAsync(source);

        source.ConsecutiveFailureCount.Should().Be(0);
        source.LastSyncError.Should().BeNull();
        source.LastSyncErrorAt.Should().BeNull();
    }

    // FR-05, IADR-0053: 直近エラーは**保存の時点で**マスクされる（表示の時点ではない）。
    [Fact]
    public async Task Sync_Failure_StoresRedactedError()
    {
        using var scope = factory.Services.CreateScope();
        var svc = BuildService(scope, new SecretLeakingConnector());
        var source = DataSource.Create("leaky", "filesystem", "");

        await svc.SyncAsync(source);

        source.LastSyncError.Should().NotBeNull();
        source.LastSyncError.Should().NotContain("hunter2", "接続文字列の秘密が平文で保存されてはならない");
        source.LastSyncError.Should().Contain("***");
    }

    private static DataSourceSyncService BuildService(
        IServiceScope scope, IDataSourceConnector connector)
    {
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var registry = new ConnectorRegistry([connector]);
        return new DataSourceSyncService(registry, storage, bus,
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

    // 接続文字列を含む例外を投げるコネクタ（資格情報が例外メッセージへ漏れる実例を模す）。
    private sealed class SecretLeakingConnector : IDataSourceConnector
    {
        public string SourceType => "filesystem";
        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(DataSource s, DateTimeOffset? since, CancellationToken ct)
            => throw new IOException("connect failed: Host=db;Username=app;Password=hunter2");
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
