using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.Persistence;

// FR-01, UC-04, IADR-0083 (#305): 非リレーショナル DB（単体テストの InMemory 等）向けの常時取得コーディネータ。
// advisory lock は Postgres 固有機能のため使えない環境で用いる。競合排除は不要（単一プロセス）なので常に取得成功を返し、
// 従来どおり毎サイクル同期する（後方互換）。本番（Npgsql）は PostgresAdvisoryLockLeaseCoordinator を用いる。
public sealed class NoOpSyncLeaseCoordinator : ISyncLeaseCoordinator
{
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
