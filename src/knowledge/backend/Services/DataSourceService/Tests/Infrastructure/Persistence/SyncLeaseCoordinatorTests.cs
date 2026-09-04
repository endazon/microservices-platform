using DataSourceService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataSourceService.Tests.Infrastructure.Persistence;

// FR-01, UC-04, IADR-0083 (#305): 定期同期の単一書き手化コーディネータの契約を検証する。
// NoOp は非リレーショナル環境で常に取得し従来どおり動く。Postgres advisory lock は取得失敗（接続不能）を
// 安全側でスキップ（null 返却・例外を投げない）する fail-safe を回帰ガードする。
[Trait("TestKind", "Unit")]
public sealed class SyncLeaseCoordinatorTests
{
    // NoOp コーディネータ: 常に取得成功（非 null）し、ハンドル破棄が例外を投げない。
    [Fact]
    public async Task NoOp_AlwaysAcquires_AndDisposeIsSafe()
    {
        var coordinator = new NoOpSyncLeaseCoordinator();

        var lease = await coordinator.TryAcquireAsync(CancellationToken.None);

        lease.Should().NotBeNull("非リレーショナル/単一プロセスは競合が無く常に取得できる（従来どおり実行）");
        var dispose = async () => await lease!.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    // Postgres advisory lock: 到達不可な接続先ではリース取得に失敗するが、例外を投げず null を返す（fail-safe）。
    // ＝取得できない周期は同期を強行せずスキップする。
    [Fact]
    public async Task PostgresAdvisoryLock_UnreachableDb_ReturnsNull_AndDoesNotThrow()
    {
        // 何も待ち受けていないローカルポートへ短いタイムアウトで接続 → 即座に失敗（接続拒否）する。
        var coordinator = new PostgresAdvisoryLockLeaseCoordinator(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1;Command Timeout=1",
            NullLogger<PostgresAdvisoryLockLeaseCoordinator>.Instance);

        IAsyncDisposable? lease = null;
        var act = async () => lease = await coordinator.TryAcquireAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("取得失敗は安全側でスキップし次周期へ（fail-safe・例外で停止しない）");
        lease.Should().BeNull("接続不能時はリースを取得できない → 本サイクルはスキップ");
    }
}
