using DataSourceService.Domain.Ports;
using Npgsql;
using NpgsqlTypes;

namespace DataSourceService.Infrastructure.Persistence;

// FR-01, UC-04, IADR-0083 (#305): PostgreSQL セッションレベル advisory lock による定期同期の単一書き手化。
// 各サイクルで専用接続を開き pg_try_advisory_lock(<固定キー>) を試行する。取得できたレプリカのみが同期し、
// ハンドル破棄時に pg_advisory_unlock + 接続破棄する。取得不可（他レプリカ保持中）／一時障害は null を返して
// 本サイクルをスキップさせる（fail-safe）。ロックはセッションに紐づくため pod crash 時はセッション終了で自動解放される。
public sealed class PostgresAdvisoryLockLeaseCoordinator(
    string connectionString,
    ILogger<PostgresAdvisoryLockLeaseCoordinator> logger) : ISyncLeaseCoordinator
{
    // 全レプリカで一致する固定キー。"DSPS"（DataSource Periodic Sync）の 4 バイト = 0x44535053。
    // advisory lock の名前空間を他用途と衝突させないための定数。
    internal const long AdvisoryLockKey = 0x44535053;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
        NpgsqlConnection? conn = null;
        try
        {
            conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", conn);
            // pg_try_advisory_lock は bigint オーバーロードを用いる。型を明示して意図を固定する。
            cmd.Parameters.AddWithValue("key", NpgsqlDbType.Bigint, AdvisoryLockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;

            if (!acquired)
            {
                // 他レプリカが同期中 → 本サイクルはスキップ（次周期で再試行）。
                await conn.DisposeAsync();
                return null;
            }

            return new AdvisoryLockLease(conn, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 一時的な取得失敗（接続不能等）は安全側でスキップし次周期へ（fail-safe）。実行を強行しない。
            logger.LogWarning(ex, "定期同期リースの取得に失敗しました。本サイクルをスキップします。");
            if (conn is not null)
                await conn.DisposeAsync();
            return null;
        }
    }

    // 取得済みリース。破棄時にアンロックし接続を閉じる。アンロック失敗でもセッション終了で自動解放される。
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
                logger.LogWarning(ex, "定期同期リースのアンロックに失敗しました（接続終了時に自動解放されます）。");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
