using GraphService.Domain.Ports;
using Npgsql;
using NpgsqlTypes;

namespace GraphService.Infrastructure.Persistence;

// FR-17, ADR-0035 決定 3, [[IADR-0425]] 決定 6 (#1363): PostgreSQL の advisory lock による
// クラスタ検出の単一書き手化。形は `PostgresKnowledgeHealthLeaseCoordinator` と同型である
// （同じ理由で複製する —— サービス間・関心事間で抽象を上げるのは本作業の射程を超える）。
public sealed class PostgresClusterDetectionLeaseCoordinator(
    string connectionString,
    ILogger<PostgresClusterDetectionLeaseCoordinator> logger) : IClusterDetectionLeaseCoordinator
{
    // 全レプリカで一致する固定キー。"GCLD"（Graph CLuster Detection）の 4 バイト。
    // 🔴 ナレッジ健全性の "GKHP" とは**別値**である —— 同値にすると日次の検出（長い）が
    // 毎時の指標報告（短い）を塞ぐ。
    internal const long AdvisoryLockKey = 0x47434C44;

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
            // 2 レプリカが同時に走ると、互いのクラスタを「消滅」と判定して消し合う。
            logger.LogWarning(ex, "クラスタ検出のリース取得に失敗した。本周期をスキップする。");
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
                    "クラスタ検出のリース解放に失敗した（接続終了時に自動解放される）。");
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
public sealed class NoOpClusterDetectionLeaseCoordinator : IClusterDetectionLeaseCoordinator
{
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
