using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
// 名前空間 Knowledge.IntegrationTests.DataSourceService の末尾セグメントと衝突するため global:: で明示する。
using global::DataSourceService.Domain;
using global::DataSourceService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DataSourceService;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 4, IADR-0501 決定 3 (#458 段 S2 の独立監査の指摘):
// 参照の配置（`PUT /datasources/{id}/credentials/{key}/reference`）が**並行する書き込みを消さない**ことを実 PostgreSQL で固定する。
//
// **単体テストでは踏めない経路である。** `DataSourceService.Tests` は EF InMemory を使っており、InMemory は SQL を実行しない。
// 「読んでから書くまでの間に入った別の書き込み」は、古い実体を持つ文脈（DbContext）を 2 つ並べて決定的に作る。
[Trait("Category", "Integration")]
public sealed class CredentialReferencePlacementTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // 🔴 テスト用の明白なダミー値（秘密ではない）。
    private const string PlaintextMarker = "plaintext-marker-for-placement-race-tests";

    private static async Task<string> CreateDatabaseAsync(string baseConnectionString)
    {
        var name = $"credref_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        builder.Database = name;
        await using (var db = NewContext(builder.ConnectionString))
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        return builder.ConnectionString;
    }

    private static DataSourceDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DataSourceDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<Guid> SeedAsync(string cs, Dictionary<string, string>? config = null)
    {
        await using var db = NewContext(cs);
        var ds = DataSource.Create("race", "db", "https://source.example.test", config);
        db.DataSources.Add(ds);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ds.Id;
    }

    private static async Task<Dictionary<string, string>> ConfigOfAsync(string cs, Guid id)
    {
        await using var db = NewContext(cs);
        return (await db.DataSources.AsNoTracking().SingleAsync(d => d.Id == id, TestContext.Current.CancellationToken)).Config;
    }

    // 🔴 別のキーへの配置が 2 つ重なっても、両方とも残る（古い辞書で丸ごと上書きしない）。
    // 2 つの文脈が先に同じ行を読み、片方が置いた後に、古い実体を持つもう片方が置く —— 従来の実装はここで先の参照を消した。
    [Fact]
    public async Task Concurrent_placements_for_different_keys_both_survive()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        if (!postgres.IsAvailable) return;
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = await SeedAsync(cs);
        var ct = TestContext.Current.CancellationToken;

        await using var first = NewContext(cs);
        await using var second = NewContext(cs);
        var staleFirst = await first.DataSources.SingleAsync(d => d.Id == id, ct);
        var staleSecond = await second.DataSources.SingleAsync(d => d.Id == id, ct);

        (await CredentialReferencePlacement.PlaceAsync(second, staleSecond, "apiToken", ct))
            .Should().Be(DataSourceCredentialSupply.Reference);
        (await CredentialReferencePlacement.PlaceAsync(first, staleFirst, "password", ct))
            .Should().Be(DataSourceCredentialSupply.Reference);

        var config = await ConfigOfAsync(cs, id);
        config["apiToken"].Should().Be(ConnectorSecretReference.CanonicalFor(id, "apiToken"));
        config["password"].Should().Be(ConnectorSecretReference.CanonicalFor(id, "password"));

        // 同時に走らせても同じ（並列の陽性対照。別の行で 8 回）。
        for (var i = 0; i < 8; i++)
        {
            var rowId = await SeedAsync(cs);
            await using var a = NewContext(cs);
            await using var b = NewContext(cs);
            var rowA = await a.DataSources.SingleAsync(d => d.Id == rowId, ct);
            var rowB = await b.DataSources.SingleAsync(d => d.Id == rowId, ct);
            await Task.WhenAll(
                CredentialReferencePlacement.PlaceAsync(a, rowA, "apiToken", ct),
                CredentialReferencePlacement.PlaceAsync(b, rowB, "password", ct));
            (await ConfigOfAsync(cs, rowId)).Keys.Should().Contain(["apiToken", "password"]);
        }
    }

    // 🔴 配置は既存の値を決して上書きしない。読んだ後に別の書き込み（SC-06 の更新）が平文を入れたら、
    // 古い実体が「値なし」と言っていても置かない。応答は実際の状態（`other`）であって `reference` ではない。
    [Fact]
    public async Task Placement_never_overwrites_a_value_written_after_it_read_the_row()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        if (!postgres.IsAvailable) return;
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = await SeedAsync(cs);
        var ct = TestContext.Current.CancellationToken;

        await using var placer = NewContext(cs);
        var stale = await placer.DataSources.SingleAsync(d => d.Id == id, ct);
        stale.CredentialSupplyOf("password").Should().Be(DataSourceCredentialSupply.Absent, "前提: 読んだ時点では値なし");

        await using (var editor = NewContext(cs))
        {
            var row = await editor.DataSources.SingleAsync(d => d.Id == id, ct);
            row.Patch(config: new Dictionary<string, string> { ["password"] = PlaintextMarker });
            await editor.SaveChangesAsync(ct);
        }

        (await CredentialReferencePlacement.PlaceAsync(placer, stale, "password", ct))
            .Should().Be(DataSourceCredentialSupply.Other);
        (await ConfigOfAsync(cs, id))["password"].Should().Be(PlaintextMarker);

        // 陽性対照: 値なし（空白だけ）なら置く。
        var blank = await SeedAsync(cs, new() { ["password"] = "  " });
        await using var fresh = NewContext(cs);
        var blankRow = await fresh.DataSources.SingleAsync(d => d.Id == blank, ct);
        (await CredentialReferencePlacement.PlaceAsync(fresh, blankRow, "password", ct))
            .Should().Be(DataSourceCredentialSupply.Reference);
        (await ConfigOfAsync(cs, blank))["password"].Should().Be(ConnectorSecretReference.CanonicalFor(blank, "password"));
    }
}
