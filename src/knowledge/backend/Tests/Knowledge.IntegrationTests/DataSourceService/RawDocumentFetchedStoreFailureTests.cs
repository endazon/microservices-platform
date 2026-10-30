using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Extensions;
// 名前空間 Knowledge.IntegrationTests.DataSourceService の末尾セグメントと衝突するため global:: で明示する。
using global::DataSourceService.Domain;
using global::DataSourceService.Domain.Ports;
using global::DataSourceService.Features.DataSources.Sync;
using global::DataSourceService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DataSourceService;

// 🔴 FR-01, UC-04, ADR-0027 §決定, IADR-0657 (#2361): **送出表（Outbox）への書き込みが失敗したら、その 1 件は失敗に数え、
// watermark を進めない。** DataSourceService の本番の配線（`Program.cs`）そのもので測る。
//
// 従前（IADR-0609 決定 2）は素の `IMessageBus.PublishAsync` を耐久の送出先へ出していた。送出表への書き込みは
// JasperFx の `RetryBlock` に渡され、**失敗すると例外なしで返る**（再試行は裏で続き、既定の上限〔最初の 1 回 ＋ 再試行 3 回・
// 約 0.5 秒〕で黙って捨てられる。#2361 の実測）。同期は発行が成功したと数えて watermark を進め、原本は恒久に欠落した。
//
// 失敗は**試験ごとの専用の DB** の送出表に置いたトリガで起こす（`OutboxStoreFailure`。片付けは finally）。
// トリガは消さずに置いたままにするので、裏の再試行もすべて失敗する —— 上限で捨てられる場合と同じ形である。
[Trait("Category", "Integration")]
public sealed class RawDocumentFetchedStoreFailureTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 送出が済んで Outbox の表が空に戻るまでの予算。
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    private const string ProbeType = "outbox-probe";

    private async Task<string> CreateDatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        var baseConnectionString = postgres.ConnectionString!;
        var name = $"dsoutbox_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = name }.ConnectionString;
    }

    // 本番の `Program.cs` を専用の DB へ向けて起こす（DbContext・Outbox・advisory lock の接続文字列はすべて同じキーから読む）。
    // コネクタだけ試験用を 1 つ足す（1 件を列挙し、1 件を返す）。
    private static WebApplicationFactory<global::DataSourceService.DataSourceServiceTestMarker> Dedicated(
        DataSourceServiceFactory root, string cs) =>
        root.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:DefaultConnection", cs);
            b.ConfigureServices(services =>
            {
                // 基底の器は DbContext を共有の DB へ差し替えている。専用の DB へ差し替え直す。
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<DataSourceDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                        .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(DataSourceDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<DataSourceDbContext>(o => o.UseNpgsql(cs));

                services.AddSingleton<IDataSourceConnector, OneItemConnector>();
            });
        });

    private static async Task<long> OutboxRowsAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"select count(*) from {WolverineExtensions.OutboxSchemaName}.wolverine_outgoing_envelopes;", conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task WaitOutboxDrainedAsync(string cs, TimeSpan budget)
    {
        var until = DateTime.UtcNow + budget;
        while (await OutboxRowsAsync(cs) > 0)
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"Outbox の表が {budget.TotalSeconds:F0} 秒以内に空にならなかった（送出が済んでいない）。");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    private static async Task<SyncResult> SyncOnceAsync(IServiceProvider services, DataSource source)
    {
        await using var scope = services.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<DataSourceSyncService>();
        return await sync.SyncAsync(source, Ct);
    }

    // 🔴 本題: 送出表への書き込みが失敗すれば、その 1 件は失敗で、watermark は進まない（次の同期で取り直す）。
    // 🔴 A（1 件ごとの確定）を外して素の `IMessageBus.PublishAsync` へ戻すと、発行は例外なしで返り、
    // `Fetched = 1`・watermark 前進でここが赤になる（#2361 の赤のコミットで確かめる）。
    [Fact]
    public async Task 送出表への書き込みが失敗すればその1件は失敗でwatermarkは進まない()
    {
        var cs = await CreateDatabaseAsync();
        await using var root = new DataSourceServiceFactory(postgres, rabbit);
        await using var factory = Dedicated(root, cs);
        _ = factory.Server; // ホストを起こす（Migration と Wolverine の表の作成は起動時に済む）。

        var source = DataSource.Create("outbox-failure", ProbeType, "");
        await OutboxStoreFailure.InstallAsync(cs, Ct);
        SyncResult result;
        try
        {
            result = await SyncOnceAsync(factory.Services, source);
        }
        finally
        {
            await OutboxStoreFailure.RemoveAsync(cs);
        }

        result.Fetched.Should().Be(0, "送出表に書けなかった発行は、取得の成功に数えない");
        result.Failed.Should().Be(1, "送出表への書き込みの失敗は、その 1 件の失敗である");
        result.ShouldAdvanceWatermark.Should().BeFalse();
        source.LastSyncedAt.Should().BeNull("watermark を進めると、次の増分同期はこの原本を見ず、恒久に欠落する");
        source.ConsecutiveFailureCount.Should().Be(1, "失敗は SC-06 の連続失敗に数える");
        (await OutboxRowsAsync(cs)).Should().Be(0, "書けなかった envelope は表に無い");
    }

    // 対照: 失敗が無ければ、発行は表を経て送られ（表は空に戻る）、watermark は進む。
    [Fact]
    public async Task 送出表に書ければ発行されてwatermarkが進む()
    {
        var cs = await CreateDatabaseAsync();
        await using var root = new DataSourceServiceFactory(postgres, rabbit);
        await using var factory = Dedicated(root, cs);
        _ = factory.Server;

        var source = DataSource.Create("outbox-success", ProbeType, "");
        var result = await SyncOnceAsync(factory.Services, source);

        result.Fetched.Should().Be(1);
        result.Failed.Should().Be(0);
        result.ShouldAdvanceWatermark.Should().BeTrue();
        source.LastSyncedAt.Should().NotBeNull();
        await WaitOutboxDrainedAsync(cs, Delivery);
    }

    // 1 件を列挙し、1 件を返すだけのコネクタ（外部へ繋がない）。
    private sealed class OneItemConnector : IDataSourceConnector
    {
        public string SourceType => ProbeType;

        public Task<IReadOnlyList<SourceItem>> DiscoverAsync(
            DataSource source, ConnectorCredentials credentials, DateTimeOffset? since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SourceItem>>([new SourceItem("/probe/a.md", DateTimeOffset.UtcNow, 2)]);

        public Task<RawContent> FetchAsync(
            DataSource source, ConnectorCredentials credentials, SourceItem item, CancellationToken ct) =>
            Task.FromResult(new RawContent("ok"u8.ToArray(), "text/markdown"));
    }
}
