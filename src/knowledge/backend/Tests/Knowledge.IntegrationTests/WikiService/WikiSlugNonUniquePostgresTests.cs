using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using global::WikiService.Domain;
using global::WikiService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.WikiService;

// FR-13, ADR-0091 決定 1, [[IADR-0644]] (#2108): **同じ slug の行を 2 つ持てる**ことを、本番と同じマイグレーションで作った表で確かめる。
//
// 単体試験は EF InMemory で、索引の一意を強制しない（モデルの宣言だけを見る）。実 PostgreSQL では、従前の一意索引 `IX_Pages_Slug` が
// 同じ slug の 2 行目を 23505 で拒んだ（ADR-0091 実測 3・IADR-0541 の起点 #1934）。マイグレーションで索引を作り直したことを実物で見る。
[Trait("Category", "Integration")]
public sealed class WikiSlugNonUniquePostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> CreateDatabaseAsync(string baseConnectionString)
    {
        var name = $"wikislug_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        builder.Database = name;
        await using (var db = NewContext(builder.ConnectionString))
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        return builder.ConnectionString;
    }

    private static WikiDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<WikiDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task 同じslugの別文書を2行とも保存でき索引は一意でない()
    {
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var first = WikiPage.CreateFromDocument(Guid.NewGuid(), "同名規程", "s3://b/a.md", new() { ["confidentiality"] = "public" }, []);
        var second = WikiPage.CreateFromDocument(Guid.NewGuid(), "同名規程", "s3://b/b.md", new() { ["confidentiality"] = "public" }, []);
        first.Slug.Should().Be(second.Slug, "前提: 同じ題名は同じ slug へ写る");

        await using (var db = NewContext(cs))
        {
            db.Pages.AddRange(first, second);
            await db.SaveChangesAsync(Ct); // 従前は IX_Pages_Slug の 23505
        }

        await using (var db = NewContext(cs))
            (await db.Pages.CountAsync(p => p.Slug == first.Slug, Ct)).Should().Be(2);

        // 索引そのものは残る（slug での引き当てのため）。一意ではない。
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'Pages' AND indexname = 'IX_Pages_Slug';", conn);
        var def = (string?)await cmd.ExecuteScalarAsync(Ct);
        def.Should().NotBeNull();
        def!.Should().NotContain("UNIQUE");
    }
}
