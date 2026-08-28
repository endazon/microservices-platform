using GraphService.Domain.Ports;
using Npgsql;
using NpgsqlTypes;

namespace GraphService.Infrastructure.Persistence;

// FR-10, FR-17, SC-10, [[IADR-0299]] 決定 3 (#443): PostgreSQL のセッションレベル advisory lock による
// 単一書き手化。周期ごとに専用接続を開き `pg_try_advisory_lock(<固定キー>)` を試す。
// 取得できたレプリカだけが報告し、ハンドルの破棄で unlock ＋ 接続破棄する。
// 取得不可（他レプリカ保持中）・一時障害は null を返して**本周期をスキップさせる**（fail-safe）。
// ロックはセッションに紐づくため、pod が落ちればセッション終了で自動解放される。
//
// **DataSourceService の PostgresAdvisoryLockLeaseCoordinator と同型の複製である**
// （サービス間の直接参照は禁止。理由は IKnowledgeHealthLeaseCoordinator の注記）。
public sealed class PostgresKnowledgeHealthLeaseCoordinator(
    string connectionString,
    ILogger<PostgresKnowledgeHealthLeaseCoordinator> logger) : IKnowledgeHealthLeaseCoordinator
{
    // 全レプリカで一致する固定キー。"GKHP"（Graph Knowledge Health Producer）の 4 バイト。
    // DataSourceService の "DSPS" とは別値にしてある —— DB は分かれている（DB-per-service）ので
    // 衝突し得ないが、**同じ値だと「同じロックを取っている」と読み違えられる**。
    internal const long AdvisoryLockKey = 0x474B4850;

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
            // 一時的な取得失敗は安全側でスキップする。**実行を強行しない** ——
            // 強行すると 2 レプリカがスナップショット置換を撃ち合い、件数が過少になる。
            logger.LogWarning(ex, "ナレッジ健全性のリース取得に失敗した。本周期をスキップする。");
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
                    "ナレッジ健全性のリース解放に失敗した（接続終了時に自動解放される）。");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

// FR-10, [[IADR-0299]] 決定 3: 非リレーショナル（単体テストの InMemory 等）向けの常時取得コーディネータ。
// advisory lock は PostgreSQL 固有機能のため使えない。単一プロセスであり競合排除は不要である。
public sealed class NoOpKnowledgeHealthLeaseCoordinator : IKnowledgeHealthLeaseCoordinator
{
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
