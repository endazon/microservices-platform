using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using global::WikiService.Domain.Ports;
using global::WikiService.Features.Wiki;
using global::WikiService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.WikiService;

// 🔴 FR-13, UC-07, ADR-0011, NFR-05, IADR-0563 残余リスク (a), IADR-0599 決定 1・2 (#2100):
// **2 つのレプリカ（2 つの門・2 つのロックの器・別々の接続）から同時に Wiki.js へ書いても、書き込みは直列になる**ことを実 PostgreSQL で測る。
//
// 窓は決定的に開く: 門 A の書き込みを TCS で止めておき、門 B が **PostgreSQL のロック待ち（`pg_locks` の未許可の advisory lock）に
// 入ったことを確かめてから** B の書き込みがまだ始まっていないことを見る（時間で待たない）。
// 対照として、レプリカをまたぐ枠を外した門（プロセスの中の枠だけ＝ #2100 の前の形）では、同じ手順で 2 本が重なることも測る
// （重なりを測れない器で「1」を見ても意味が無い。排他を外すと赤になることの証拠）。
[Trait("Category", "Integration")]
public sealed class WikiJsReplicaWriteLockTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static readonly ConsumerCallTimeouts Calls = new ServiceCollection()
        .AddLogging().AddPlatformConsumerTimeouts().BuildServiceProvider().GetRequiredService<ConsumerCallTimeouts>();

    // advisory lock はデータベースの単位で効く。試験ごとに専用の DB を作り、他の試験のロックと混ざらないようにする。
    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"wikigate_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = name }.ConnectionString;
    }

    private static PostgresWikiJsReplicaWriteLock Replica(string cs) =>
        new(cs, NullLogger<PostgresWikiJsReplicaWriteLock>.Instance);

    // 未許可（待ち中）・許可済みの advisory lock の数。
    private static async Task<(long Waiting, long Granted)> AdvisoryLocksAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE NOT granted), count(*) FILTER (WHERE granted) FROM pg_locks "
            + "WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(because);
            await Task.Delay(20, Ct);
        }
    }

    // Wiki.js への書き込みの重なりを数える器。最初の 1 本は試験が開けるまで止まる（窓を開けたままにする）。
    private sealed class OverlapProbe
    {
        private int _inFlight;
        private int _maxOverlap;
        private int _entered;
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxOverlap => Volatile.Read(ref _maxOverlap);
        public int Entered => Volatile.Read(ref _entered);

        public async Task WriteAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxOverlap)))
                Interlocked.CompareExchange(ref _maxOverlap, now, seen);
            var order = Interlocked.Increment(ref _entered);
            try
            {
                if (order == 1)
                {
                    FirstEntered.SetResult();
                    await FirstRelease.Task.WaitAsync(ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static Task WriteThrough(WikiJsWriteGate gate, OverlapProbe probe, string step) =>
        gate.RunAsync(Calls, step, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), probe.WriteAsync, Ct);

    // 受け入れ基準 1: 2 つのレプリカの門（同期と撤去）から同時に書いても、Wiki.js への書き込みは 1 本ずつになる。
    [Fact]
    public async Task 二つのレプリカの門から同時に書いても書き込みは直列になる()
    {
        var cs = await CreateDatabaseAsync();
        using var replicaA = new WikiJsWriteGate(1, Replica(cs));
        using var replicaB = new WikiJsWriteGate(1, Replica(cs));
        var probe = new OverlapProbe();

        var a = WriteThrough(replicaA, probe, "wiki-sync");
        await probe.FirstEntered.Task.WaitAsync(Deadline, Ct);
        var b = WriteThrough(replicaB, probe, "wiki-delete");

        // B は PostgreSQL のロック待ちに入っている（時間で待たず、待ちそのものを観測する）。その間、B の書き込みは始まらない。
        await WaitUntilAsync(async () => (await AdvisoryLocksAsync(cs)).Waiting == 1, "門 B が advisory lock の待ちに入らなかった");
        probe.Entered.Should().Be(1, "A が書いている間、別のレプリカの B は Wiki.js へ書き込まない");

        probe.FirstRelease.SetResult();
        await Task.WhenAll(a, b).WaitAsync(Deadline, Ct);

        probe.Entered.Should().Be(2);
        probe.MaxOverlap.Should().Be(1);
        (await AdvisoryLocksAsync(cs)).Should().Be((0L, 0L), "書き終えたら枠はすべて返っている");
    }

    // 対照（排他を外した形 ＝ #2100 の前の、プロセスの中の枠だけの門）: 同じ手順で 2 本が重なる。
    [Fact]
    public async Task 対照_プロセスの中の枠だけの門は二つのレプリカで重なる()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        using var replicaA = new WikiJsWriteGate(1);
        using var replicaB = new WikiJsWriteGate(1);
        var probe = new OverlapProbe();

        var a = WriteThrough(replicaA, probe, "wiki-sync");
        await probe.FirstEntered.Task.WaitAsync(Deadline, Ct);
        var b = WriteThrough(replicaB, probe, "wiki-delete");
        await WaitUntilAsync(() => Task.FromResult(probe.Entered == 2), "B が書き込みに入らなかった");

        probe.MaxOverlap.Should().Be(2, "プロセスの中の枠はレプリカをまたがない（#1971 の衝突が戻る形）");
        probe.FirstRelease.SetResult();
        await Task.WhenAll(a, b).WaitAsync(Deadline, Ct);
    }

    // 待ちの上限: 他のレプリカが枠を持ち続けると、門の待ちの期限で `wiki-js-gate` の時間切れになる（再試行へ委ねる）。
    // 待ちを切った後、待っていた接続は閉じられ、未許可のロックも許可済みのロックも残らない。持ち主が返せば次は直ちに取れる。
    [Fact]
    public async Task ロック待ちは門の待ちの期限で切れ枠も接続も残らない()
    {
        var cs = await CreateDatabaseAsync();
        var holderLock = Replica(cs);
        using var waiter = new WikiJsWriteGate(1, Replica(cs));
        var holder = await holderLock.AcquireAsync(1, Ct);

        var act = () => waiter.RunAsync(Calls, "wiki-delete", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15),
            _ => Task.CompletedTask, Ct);
        (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which.Target.Should().Be(WikiSyncTimeouts.WikiJsGateTarget);

        await WaitUntilAsync(async () => (await AdvisoryLocksAsync(cs)) == (0L, 1L), "切った待ちのロックが残っている");

        await holder.DisposeAsync();
        await holder.DisposeAsync(); // 破棄はべき等（2 回目は何もしない）
        (await AdvisoryLocksAsync(cs)).Should().Be((0L, 0L));

        var ran = false;
        await waiter.RunAsync(Calls, "wiki-delete", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            _ => { ran = true; return Task.CompletedTask; }, Ct);
        ran.Should().BeTrue("持ち主が返した後は待たずに通る");
    }

    // 枠の数（`Wiki:MaxParallelWrites`）はレプリカをまたいで合わせた数である: 2 なら 2 本までは待たずに取れ、3 本目は空くまで取れない。
    // どちらの枠が空いても 3 本目は取れる（1 本を選んで待つ形では、選ばなかった枠が空いても待ち続ける）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 枠の数はレプリカをまたいで合わせた数である(bool releaseFirst)
    {
        var cs = await CreateDatabaseAsync();
        var first = await Replica(cs).AcquireAsync(2, Ct);
        var second = await Replica(cs).AcquireAsync(2, Ct);
        (await AdvisoryLocksAsync(cs)).Should().Be((0L, 2L));

        var third = Replica(cs).AcquireAsync(2, Ct);
        await Task.Delay(PostgresWikiJsReplicaWriteLock.PollInterval * 3, Ct);
        third.IsCompleted.Should().BeFalse("2 本とも持たれている間は 3 本目は取れない");

        await (releaseFirst ? first : second).DisposeAsync();
        await using var thirdLease = await third.WaitAsync(Deadline, Ct);
        (await AdvisoryLocksAsync(cs)).Granted.Should().Be(2);
        await (releaseFirst ? second : first).DisposeAsync();
    }
}
