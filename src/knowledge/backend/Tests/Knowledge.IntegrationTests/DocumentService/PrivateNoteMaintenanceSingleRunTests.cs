using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using global::DocumentService.Domain.Ports;
using global::DocumentService.Features.PrivateNotes.Maintenance;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// 🔴 FR-19, FR-20, FR-22, ADR-0096 決定 1・2, IADR-0599 決定 5 (#2100):
// **2 つのホスト（レプリカ）で個人資料の定期処理を起動しても、周期の本体を回すのは 1 つだけ**であることを実 PostgreSQL で測る。
//
// 窓は決定的に開く: ホスト A の周期の本体を TCS で止めておき（リースを持ったまま）、その間にホスト B の周期を回す。
// B はリースを取れず本体を回さずに戻る。A が終われば次の周期では B が取れる（1 レプリカが止まっても処理は止まらない）。
// 対照として、リースを外した形（常時取得 ＝ #2100 の前と同じく誰でも回す）では、同じ手順で 2 つとも本体へ入ることも測る。
[Trait("Category", "Integration")]
public sealed class PrivateNoteMaintenanceSingleRunTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // advisory lock はデータベースの単位で効く。試験ごとに専用の DB を作る。
    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"pnmaint_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = name }.ConnectionString;
    }

    private static IServiceScopeFactory Scopes() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    // 周期の本体の入り数を数える器。最初の 1 回は試験が開けるまで止まる（リースを持ったままにする）。
    private sealed class CycleProbe
    {
        private int _entered;
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered => Volatile.Read(ref _entered);

        public async Task RunAsync(IServiceProvider _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) == 1)
            {
                FirstEntered.SetResult();
                await FirstRelease.Task.WaitAsync(ct);
            }
        }
    }

    private static PrivateNoteMaintenanceHostedService Host(IPrivateNoteMaintenanceLeaseCoordinator coordinator, CycleProbe probe) =>
        new(Scopes(), coordinator, NullLogger<PrivateNoteMaintenanceHostedService>.Instance) { CycleBody = probe.RunAsync };

    private static PostgresPrivateNoteMaintenanceLeaseCoordinator Replica(string cs) =>
        new(cs, NullLogger<PostgresPrivateNoteMaintenanceLeaseCoordinator>.Instance);

    // 受け入れ基準 2: 2 つのホストで同じ周期を回しても、本体を回すのは 1 つだけ。
    [Fact]
    public async Task 二つのホストで周期を回しても本体を回すのは一つだけ()
    {
        var cs = await CreateDatabaseAsync();
        var probe = new CycleProbe();
        var hostA = Host(Replica(cs), probe);
        var hostB = Host(Replica(cs), probe);

        var a = hostA.TryRunCycleAsync(Ct);
        await probe.FirstEntered.Task.WaitAsync(Deadline, Ct);

        (await hostB.TryRunCycleAsync(Ct)).Should().BeFalse("A がリースを持っている周期では B は本体を回さない");
        probe.Entered.Should().Be(1);

        probe.FirstRelease.SetResult();
        (await a.WaitAsync(Deadline, Ct)).Should().BeTrue();

        // A が返した後の周期では B が取れる（liveness）。
        (await hostB.TryRunCycleAsync(Ct)).Should().BeTrue("A が返した後は B が回せる");
        probe.Entered.Should().Be(2);
    }

    // 対照（排他を外した形）: リースが常に取れると、同じ手順で 2 つとも本体へ入る。
    [Fact]
    public async Task 対照_リースを外すと二つのホストが同じ周期に本体へ入る()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var probe = new CycleProbe();
        var hostA = Host(NoOpPrivateNoteMaintenanceLeaseCoordinator.Instance, probe);
        var hostB = Host(NoOpPrivateNoteMaintenanceLeaseCoordinator.Instance, probe);

        var a = hostA.TryRunCycleAsync(Ct);
        await probe.FirstEntered.Task.WaitAsync(Deadline, Ct);

        (await hostB.TryRunCycleAsync(Ct)).Should().BeTrue();
        probe.Entered.Should().Be(2, "リースが無いと 2 つのレプリカが同じ周期に本体を回す（#2100 の前の形）");

        probe.FirstRelease.SetResult();
        await a.WaitAsync(Deadline, Ct);
    }

    // 本体が失敗してもリースは返る（返らなければ以後どのレプリカも周期を回せない）。
    [Fact]
    public async Task 本体が失敗してもリースは返る()
    {
        var cs = await CreateDatabaseAsync();
        var failing = new PrivateNoteMaintenanceHostedService(Scopes(), Replica(cs),
            NullLogger<PrivateNoteMaintenanceHostedService>.Instance)
        { CycleBody = (_, _) => throw new InvalidOperationException("本体の失敗") };

        var act = () => failing.TryRunCycleAsync(Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var probe = new CycleProbe();
        probe.FirstRelease.SetResult();
        (await Host(Replica(cs), probe).TryRunCycleAsync(Ct)).Should().BeTrue();
    }
}
