using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
// 名前空間 Knowledge.IntegrationTests.DataSourceService の末尾セグメントと衝突するため global:: で明示する。
using global::DataSourceService.Domain;
using global::DataSourceService.Infrastructure.Persistence;
using global::DataSourceService.Infrastructure.Secrets.Migration;

namespace Knowledge.IntegrationTests.DataSourceService;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, IADR-0664 決定 3 (#458 段 S4): 移送の**設定の置き換え**（1 文の条件つき更新）を実 PostgreSQL で固定する。
// 単体テスト（EF InMemory）は SQL を実行しないので、ここでしか踏めない。
//   - 読んだ平文と今の値が同じときだけ、そのキー 1 つを正規の参照へ置き換える（同じ行の別のキーを消さない）。
//   - 読んでから書くまでの間に SC-06 の更新が入って値が変わった行は、置き換えない（新しい値を古い平文の移送で消さない）。
[Trait("Category", "Integration")]
public sealed class CredentialMigrationStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // 🔴 テスト用の明白なダミー値（秘密ではない）。
    private const string Plain = "plaintext-marker-for-migration-store-tests";

    private static async Task<string> CreateDatabaseAsync(string baseConnectionString)
    {
        var name = $"credmig_{Guid.NewGuid():N}";
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

    private static async Task<Guid> SeedAsync(string cs, Dictionary<string, string> config)
    {
        await using var db = NewContext(cs);
        var ds = DataSource.Create("migrate", "db", "https://source.example.test", config);
        db.DataSources.Add(ds);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ds.Id;
    }

    private static async Task<Dictionary<string, string>> ConfigOfAsync(string cs, Guid id)
    {
        await using var db = NewContext(cs);
        return (await db.DataSources.AsNoTracking().SingleAsync(d => d.Id == id, TestContext.Current.CancellationToken)).Config;
    }

    [Fact]
    public async Task Replaces_only_that_key_when_the_value_is_still_the_plaintext_that_was_read()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        if (!postgres.IsAvailable) return;
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = await SeedAsync(cs, new() { ["password"] = Plain, ["database"] = "app", ["apiKey"] = "second-plain" });
        var ct = TestContext.Current.CancellationToken;

        await using (var db = NewContext(cs))
            (await CredentialMigrationStore.ReplaceAsync(db, id, "password", Plain, ct)).Should().BeTrue();

        var config = await ConfigOfAsync(cs, id);
        config["password"].Should().Be(ConnectorSecretReference.CanonicalFor(id, "password"));
        config["database"].Should().Be("app", "同じ行の別のキーを消さない");
        config["apiKey"].Should().Be("second-plain", "そのキー 1 つだけを置き換える");
    }

    [Fact]
    public async Task Does_not_replace_when_the_value_changed_after_it_was_read()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        if (!postgres.IsAvailable) return;
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = await SeedAsync(cs, new() { ["password"] = Plain });
        var ct = TestContext.Current.CancellationToken;

        // 移送が読んだ後に、SC-06 の更新で値が変わった（別の文脈で書く）。
        await using (var other = NewContext(cs))
        {
            var ds = await other.DataSources.SingleAsync(d => d.Id == id, ct);
            ds.Patch(null, null, null, new Dictionary<string, string> { ["password"] = "rotated-after-read" }, null, null);
            await other.SaveChangesAsync(ct);
        }

        await using (var db = NewContext(cs))
            (await CredentialMigrationStore.ReplaceAsync(db, id, "password", Plain, ct)).Should().BeFalse();

        (await ConfigOfAsync(cs, id))["password"].Should().Be("rotated-after-read", "新しい値を古い平文の移送で消さない");
    }
}
