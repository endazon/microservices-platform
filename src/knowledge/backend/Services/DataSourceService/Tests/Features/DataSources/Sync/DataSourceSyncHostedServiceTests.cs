using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using DataSourceService.Domain.Ports;
using DataSourceService.Features.DataSources;
using DataSourceService.Features.DataSources.Sync;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataSourceService.Tests.Features.DataSources.Sync;

// FR-01, UC-04, IADR-0083 (#305): 定期同期ワーカーの単一書き手化ゲートを検証する。
// リースを取得できたレプリカのみが同期を実行し、取得できない周期はスキップする（本番マルチレプリカでの冗長 fetch 排除）。
// 各テストは独立した InMemory DB を持つ専用 factory を用いる（ワーカーは有効化するが、TryRunCycleAsync を直接 1 回だけ回す）。
[Trait("TestKind", "Integration")]
public sealed class DataSourceSyncHostedServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // リース取得成功 → 1 サイクルで active データソースを同期し watermark（LastSyncedAt）が前進、リースが解放される。
    [Fact]
    public async Task Cycle_WhenLeaseAcquired_RunsSync_AdvancesWatermark_AndReleasesLease()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient(); // ホスト（＝テストバス）を起動する。
        var dir = CreateTempDirWithFile("ok.md", "ok");
        var id = await SeedActiveFilesystemSourceAsync(factory, "granted-share", dir);

        var lease = new RecordingLease();
        var worker = BuildWorker(factory, new GrantingCoordinator(lease));

        var ran = await worker.TryRunCycleAsync(CancellationToken.None);

        ran.Should().BeTrue("リースを取得できたレプリカは同期を実行する");
        (await ReloadLastSyncedAtAsync(factory, id))
            .Should().NotBeNull("完全成功で watermark が前進する（同期が実行された証跡）");
        lease.Disposed.Should().BeTrue("同期後にリースを解放する（次周期で他レプリカも取得できる）");
    }

    // リース取得失敗（他レプリカが実行中）→ 同期は実行されず watermark も前進しない（本サイクルはスキップ・fail-safe）。
    [Fact]
    public async Task Cycle_WhenLeaseDenied_SkipsSync_DoesNotAdvanceWatermark()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var dir = CreateTempDirWithFile("skip.md", "skip");
        var id = await SeedActiveFilesystemSourceAsync(factory, "denied-share", dir);

        var worker = BuildWorker(factory, new DenyingCoordinator());

        var ran = await worker.TryRunCycleAsync(CancellationToken.None);

        ran.Should().BeFalse("リースを取得できない周期はスキップする（他レプリカが単一書き手）");
        (await ReloadLastSyncedAtAsync(factory, id))
            .Should().BeNull("同期を実行しないため watermark は前進しない（冗長 fetch を出さない）");
    }

    private static DataSourceSyncHostedService BuildWorker(
        TestWebApplicationFactory factory, ISyncLeaseCoordinator coordinator) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            coordinator,
            // SC-06: 次回同期の位相（本テストの対象はリースのゲートなので、記録先を渡すだけ）。
            factory.Services.GetRequiredService<SyncSchedule>(),
            Options.Create(new DataSourceSyncOptions { Enabled = true }),
            NullLogger<DataSourceSyncHostedService>.Instance);

    private async Task<Guid> SeedActiveFilesystemSourceAsync(
        TestWebApplicationFactory factory, string name, string rootPath)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var ds = DataSource.Create(name, "filesystem", "",
            new Dictionary<string, string> { ["rootPath"] = rootPath });
        db.DataSources.Add(ds);
        await db.SaveChangesAsync();
        return ds.Id;
    }

    private static async Task<DateTimeOffset?> ReloadLastSyncedAtAsync(TestWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var ds = await db.DataSources.AsNoTracking().SingleAsync(d => d.Id == id);
        return ds.LastSyncedAt;
    }

    private string CreateTempDirWithFile(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kp-hs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        _tempDirs.Add(dir);
        return dir;
    }

    // 取得成功を返し、破棄されたかを記録するリース（解放されることの検証用）。
    private sealed class RecordingLease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrantingCoordinator(IAsyncDisposable lease) : ISyncLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(lease);
    }

    private sealed class DenyingCoordinator : ISyncLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
