using DocumentService.Domain.Ports;
using Npgsql;
using NpgsqlTypes;

namespace DocumentService.Infrastructure.Persistence;

// FR-19, FR-20, FR-22, [[IADR-0599]] 決定 5 (#2100): PostgreSQL のセッションレベル advisory lock による定期処理の単一実行。
// 周期ごとに専用接続を開き `pg_try_advisory_lock(<固定キー>)` を試す。取得できたレプリカだけが処理し、ハンドルの破棄で unlock ＋ 接続破棄する。
// 取得不可（他レプリカ保持中）・一時障害は null を返して**本周期をスキップさせる**（fail-safe）。
// ロックはセッションに紐づくため、pod が落ちればセッション終了で自動解放される。
//
// **GraphService の `PostgresKnowledgeHealthLeaseCoordinator`（[[IADR-0299]] 決定 3）と同型の複製である**
// （サービス間の直接参照は禁止。理由は `IPrivateNoteMaintenanceLeaseCoordinator` の注記）。違いはキー・文言と、
// **接続をプールしない（`Pooling=false`）**ことだけである —— 解放に失敗した接続がロックを持ったままプールに眠ると、
// 次にその接続が使われるまで全レプリカが周期を飛ばし続ける。プールしなければ接続を閉じた時点でセッションごとロックが消える
// （wiki-service の書き込みの枠と同じ判断。日次の処理なので接続の開閉の費用は無視できる）。
public sealed class PostgresPrivateNoteMaintenanceLeaseCoordinator(
    string connectionString,
    ILogger<PostgresPrivateNoteMaintenanceLeaseCoordinator> logger) : IPrivateNoteMaintenanceLeaseCoordinator
{
    // 全レプリカで一致する固定キー。"DPNM"（Document Private Note Maintenance）の 4 バイト。
    // GraphService の "GKHP"・DataSourceService の "DSPS" とは別値にしてある（DB は分かれているので衝突しないが、読み違えを避ける）。
    internal const long AdvisoryLockKey = 0x44504E4D;

    private readonly string _connectionString =
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
        NpgsqlConnection? conn = null;
        try
        {
            conn = new NpgsqlConnection(_connectionString);
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
            // 一時的な取得失敗は安全側でスキップする。**実行を強行しない** —— 強行すると 2 レプリカが同じ周期に並び得る。
            logger.LogWarning(ex, "個人資料の定期処理のリース取得に失敗した。本周期をスキップする。");
            if (conn is not null)
                await conn.DisposeAsync();
            return null;
        }
        catch (OperationCanceledException)
        {
            // 停止要求の取り消しは外へ出すが、接続は閉じる（取り消しと許可が行き違って取れていても、セッションごとロックが消える）。
            if (conn is not null)
                await conn.DisposeAsync();
            throw;
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
                    "個人資料の定期処理のリース解放に失敗した（接続終了時に自動解放される）。");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

// FR-19, [[IADR-0599]] 決定 5: 非リレーショナル（単体試験の InMemory 等）向けの常時取得コーディネータ。
// advisory lock は PostgreSQL 固有機能のため使えない。単一プロセスであり競合排除は不要である。
public sealed class NoOpPrivateNoteMaintenanceLeaseCoordinator : IPrivateNoteMaintenanceLeaseCoordinator
{
    public static readonly NoOpPrivateNoteMaintenanceLeaseCoordinator Instance = new();

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static readonly NoOpLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
