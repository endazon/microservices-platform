using Npgsql;
using NpgsqlTypes;
using WikiService.Domain.Ports;

namespace WikiService.Infrastructure.Persistence;

// 🔴 FR-13, UC-07, ADR-0011, NFR-05, [[IADR-0599]] 決定 1・2 (#2100): PostgreSQL のセッションレベル advisory lock による、
// レプリカをまたぐ Wiki.js への書き込みの枠。GraphService の `PostgresKnowledgeHealthLeaseCoordinator`（[[IADR-0299]] 決定 3）と
// 同じ仕組み（専用接続・固定キー・セッションに紐づくロック）で、違いは**取れるまで待つ**ことだけである
// （周期処理は取れなければ本周期を飛ばせばよいが、受け口のメッセージは飛ばせない）。
//
// - **接続は取得ごとに 1 本を専用に開き、プールに戻さない（`Pooling=false`）。** advisory lock はセッションに紐づく。プールへ戻すと、
//   解放に失敗した接続がロックを持ったままプールに眠り、他の受け口が誰も書けなくなる。プールしなければ、接続を閉じた時点で
//   サーバ側のセッションが終わりロックも必ず消える（pod が落ちたときも同じ）。書き込みは 1 件数秒の頻度なので、接続の開閉の費用は無視できる。
// - 枠の数 `slots`（`Wiki:MaxParallelWrites`。既定 1）はキー `BaseKey + 0 … slots-1` の本数で表す。
//   - **既定の 1 本**は `pg_advisory_lock` で待つ（PostgreSQL のロック待ちは到着順に許可されるので、全レプリカで先着順になる）。
//   - 2 本以上（Wiki.js が並びに耐える版のときだけ使う。[[IADR-0563]] 決定 1）は、全部を `pg_try_advisory_lock` で試し、どれも取れなければ
//     `PollInterval` おいて試し直す。1 本を選んで待つと、別の枠が空いても選んだ枠が空くまで待ち続けるためである（順番は保証しない）。
// - 待ちは呼び出し元の ct で切る。Npgsql は ct が立つとサーバへ取り消し要求を送り、待ちの文を止める。取り消しと許可が行き違って
//   ロックを取ったまま例外になっても、接続を閉じるのでロックは残らない。
// - 破棄は `pg_advisory_unlock` の後に接続を閉じる。unlock が失敗しても閉じればロックは消える（ログだけ残す）。2 回目以降の破棄は何もしない。
public sealed class PostgresWikiJsReplicaWriteLock(
    string connectionString,
    ILogger<PostgresWikiJsReplicaWriteLock> logger) : IWikiJsReplicaWriteLock
{
    // 全レプリカで一致する固定キーの基点。"WJWG"（Wiki.js Write Gate）の 4 バイトの後ろに枠の番号 1 バイトを置く。
    // GraphService の "GKHP"・DataSourceService の "DSPS" とは別値である（DB は分かれているので衝突しないが、読み違えを避ける）。
    public const long BaseKey = 0x574A574700L;

    // 解放の文の上限（秒）。止まった接続で破棄が戻らないことを避ける（閉じればロックは消える）。
    private const int UnlockCommandTimeoutSeconds = 5;

    private readonly string _connectionString =
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    // 2 本以上のときの試し直しの間隔。
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<IAsyncDisposable> AcquireAsync(int slots, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        var conn = new NpgsqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);

            if (slots == 1)
            {
                await using var waitCmd = Command(conn, "SELECT pg_advisory_lock(@key)", BaseKey);
                waitCmd.CommandTimeout = 0; // 待ちの上限は ct（門の待ちの期限）だけが決める。
                await waitCmd.ExecuteNonQueryAsync(ct);
                return new Lease(conn, BaseKey, logger);
            }

            while (true)
            {
                for (var slot = 0; slot < slots; slot++)
                {
                    var key = BaseKey + slot;
                    await using var tryCmd = Command(conn, "SELECT pg_try_advisory_lock(@key)", key);
                    if ((bool)(await tryCmd.ExecuteScalarAsync(ct))!)
                        return new Lease(conn, key, logger);
                }
                // どの枠も他の受け口（他のレプリカを含む）が持っている。間をおいて試し直す。
                await Task.Delay(PollInterval, ct);
            }
        }
        catch
        {
            // 待ちの取り消し・接続の失敗のどちらでも、接続を閉じてセッションごとロック（取れていれば）を消す。
            await conn.DisposeAsync();
            throw;
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, string sql, long key)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Bigint, key);
        return cmd;
    }

    private sealed class Lease(NpgsqlConnection conn, long key, ILogger logger) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                await using var cmd = Command(conn, "SELECT pg_advisory_unlock(@key)", key);
                cmd.CommandTimeout = UnlockCommandTimeoutSeconds;
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Wiki.js への書き込みの枠（advisory lock {Key}）の解放に失敗した。接続を閉じるのでロックはセッションの終了で消える。", key);
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

// FR-13, [[IADR-0599]] 決定 2: 非リレーショナル（単体試験の InMemory）向け。レプリカをまたぐ枠は持たず、プロセスの中の枠
// （`WikiJsWriteGate` の `SemaphoreSlim`）だけで抑える。**本番の配線では選ばれない**（DB プロバイダがリレーショナルなら Postgres の枠）。
public sealed class ProcessLocalWikiJsReplicaWriteLock : IWikiJsReplicaWriteLock
{
    public static readonly ProcessLocalWikiJsReplicaWriteLock Instance = new();

    private ProcessLocalWikiJsReplicaWriteLock() { }

    public Task<IAsyncDisposable> AcquireAsync(int slots, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(NoLease.Instance);

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
