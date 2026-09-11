using GraphService.Domain.Ports;
using Npgsql;
using NpgsqlTypes;

namespace GraphService.Infrastructure.Persistence;

// FR-17, FR-18, ADR-0035 決定 3, [[IADR-0430]] 決定 5 (#1395): PostgreSQL の advisory lock による
// クラスタ要約生成の単一書き手化。形は `PostgresClusterDetectionLeaseCoordinator` と同型である
// （同じ理由で複製する —— 関心事間で抽象を上げるのは本作業の射程を超える）。
public sealed class PostgresClusterSummaryLeaseCoordinator(
    string connectionString,
    ILogger<PostgresClusterSummaryLeaseCoordinator> logger) : IClusterSummaryLeaseCoordinator
{
    // 全レプリカで一致する固定キー。"GCSM"（Graph Cluster SuMmary）の 4 バイト。
    // 🔴 検出の "GCLD"（0x47434C44）・健全性の "GKHP" とは**別値**である ——
    // 同値にすると LLM の応答を待つ長い処理が、他の定期処理を丸ごと塞ぐ。
    internal const long AdvisoryLockKey = 0x4743534D;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
        NpgsqlConnection? conn = null;
        try
        {
            conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", conn);
            cmd.Parameters.AddWithValue("key", NpgsqlDbType.Bigint, AdvisoryLockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;

            if (!acquired)
            {
                await conn.DisposeAsync();
                return null;
            }

            return new AdvisoryLockLease(conn, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 一時的な取得失敗は安全側でスキップする。**強行しない** ——
            // 2 レプリカが同時に走ると同じ要約を二重に生成し、費用だけが倍になる。
            logger.LogWarning(ex, "クラスタ要約生成のリース取得に失敗した。本周期をスキップする。");
            if (conn is not null)
                await conn.DisposeAsync();
            return null;
        }
    }

    private sealed class AdvisoryLockLease(NpgsqlConnection conn, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", conn);
                cmd.Parameters.AddWithValue("key", NpgsqlDbType.Bigint, AdvisoryLockKey);
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "クラスタ要約生成のリース解放に失敗した（接続終了時に自動解放される）。");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

// 非リレーショナル（単体テストの InMemory 等）向けの常時取得コーディネータ。
// advisory lock は PostgreSQL 固有機能であり、単一プロセスでは競合排除も要らない。
public sealed class NoOpClusterSummaryLeaseCoordinator : IClusterSummaryLeaseCoordinator
{
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
