using FluentAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
// 名前空間 Knowledge.IntegrationTests.DataSourceService の末尾セグメントと衝突するため global:: で明示する。
using global::DataSourceService.Api.Foundation.Services;

namespace Knowledge.IntegrationTests.DataSourceService;

// FR-01, UC-04, IADR-0083 (#305): 定期同期の単一書き手化の核心を実 PostgreSQL で回帰ガードする。
// PostgresAdvisoryLockLeaseCoordinator が pg_try_advisory_lock により「同時刻に 1 レプリカのみ取得成功」
// （＝本番マルチレプリカでも 1 サイクル 1 fetch）になること、および解放後は別レプリカが取得できる（liveness）ことを
// 実コンテナで検証する。Docker 不在時は [DockerFact] がスキップする（CI は Docker あり）。
[Trait("Category", "Integration")]
public sealed class DataSourceSyncSingleWriterTests
{
    [DockerFact]
    public async Task TwoCoordinators_ContendForSameLease_OnlyOneAcquires_ReleasesForNextCycle()
    {
        var pg = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("single_writer_test")
            .WithUsername("kp")
            .WithPassword("kp")
            .Build();
        await pg.StartAsync();
        try
        {
            var cs = pg.GetConnectionString();
            // 2 レプリカを模す独立したコーディネータ（各々が専用接続＝別セッションを張る）。
            var replicaA = new PostgresAdvisoryLockLeaseCoordinator(
                cs, NullLogger<PostgresAdvisoryLockLeaseCoordinator>.Instance);
            var replicaB = new PostgresAdvisoryLockLeaseCoordinator(
                cs, NullLogger<PostgresAdvisoryLockLeaseCoordinator>.Instance);

            // 1) 先にサイクルへ入ったレプリカ A はリースを取得できる。
            var leaseA = await replicaA.TryAcquireAsync(CancellationToken.None);
            leaseA.Should().NotBeNull("最初のレプリカは advisory lock を取得できる");

            // 2) 保持中は同時刻の別レプリカ B は取得できず本サイクルをスキップする（＝原本 fetch は 1 回・単一書き手）。
            var leaseBWhileHeld = await replicaB.TryAcquireAsync(CancellationToken.None);
            leaseBWhileHeld.Should().BeNull("保持中は他レプリカが取得できない（冗長 fetch を出さない）");

            // 3) A が解放すると、次周期では別レプリカ B が取得できる（liveness: 単一レプリカ停止でも同期が止まらない）。
            await leaseA!.DisposeAsync();
            var leaseBAfterRelease = await replicaB.TryAcquireAsync(CancellationToken.None);
            leaseBAfterRelease.Should().NotBeNull("解放後は別レプリカが取得でき、次周期の同期が継続する");
            await leaseBAfterRelease!.DisposeAsync();
        }
        finally
        {
            await pg.DisposeAsync();
        }
    }
}
