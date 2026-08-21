using DocumentService.Api.Foundation.Persistence;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-06, FR-09, SC-05, SC-09, #635: 表示名から識別子へのデータ移行（実 PostgreSQL）。
//
// **本リポジトリで最初のデータ移行つきマイグレーションである**
// （着手時の実測: `grep "Sql(" Migrations/*.cs` は 0 件）。
//
// **単体テストでは 1 行も走らない。** `DocumentService.Api.Tests` は EF InMemory を使っており、
// **InMemory プロバイダはマイグレーションの SQL を実行しない**。したがって
// 「既存の `["経理","規程"]` が識別子の配列へ書き換わる」ことは**実 DB でしか確かめられない**
// （#634 の一意インデックスが InMemory で強制されないのと同じ型の限界である）。
//
// **失敗したときの被害が大きい経路でもある。** 書き換えに漏れがあると、その行は以後
// `List<Guid>` へ復元できず、文書の取得が実行時に壊れる。
[Trait("Category", "Integration")]
public sealed class TagIdentityMigrationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // 移行の直前の状態（#634 まで）。ここまで適用してから旧形式のデータを入れる。
    private const string BeforeMigration = "20260809092529_AddTagDictionary";

    // 検証対象。
    private const string TargetMigration = "20260809123339_MigrateTagsToIdentifiers";

    // **テストごとに専用のデータベースを作る。** 移行はスキーマ全体に対する一度きりの操作であり、
    // 共有 DB では「移行前の状態」を作れない。
    private static async Task<string> CreateDatabaseAsync(string baseConnectionString, string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        builder.Database = name;
        return builder.ConnectionString;
    }

    private static DocumentDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DocumentDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    [DockerFact]
    public async Task Migration_RewritesDisplayNamesToIdentifiers()
    {
        if (!postgres.IsAvailable) return;

        var cs = await CreateDatabaseAsync(postgres.ConnectionString!,
            $"tagmig_{Guid.NewGuid():N}");

        // ── 移行前の状態を作る ──────────────────────────────────────────
        await using (var db = NewContext(cs))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(BeforeMigration);
        }

        var docKeep = Guid.NewGuid();      // 順序・重複を保つ文書
        var docBlank = Guid.NewGuid();     // 空文字だけを持つ文書（`[]` になる）
        var docEmpty = Guid.NewGuid();     // 元から空（触らない）
        var docWhitespace = Guid.NewGuid();// 全角空白で囲まれた名前（正規化して同一視する）

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();

            // **辞書には「規程」だけを先に入れておく。**
            // 移行が既存エントリを重複登録しないこと（`NOT EXISTS`）も同時に見る。
            await ExecAsync(conn, """
                INSERT INTO "Tags" ("Id", "Name", "CreatedAt")
                VALUES (gen_random_uuid(), '規程', now());
                """);

            await InsertDocumentAsync(conn, docKeep, "順序保持", """["経理", "規程", "経理"]""");
            await InsertDocumentAsync(conn, docBlank, "空文字のみ", """["", "   "]""");
            await InsertDocumentAsync(conn, docEmpty, "元から空", "[]");
            // U+3000（全角空白）で囲む。`btrim` の既定では落ちない文字である。
            await InsertDocumentAsync(conn, docWhitespace, "全角空白", "[\"　経理　\"]");

            // 版履歴にしか現れない名前も辞書へ登録される（過去版も識別子へ移すため）。
            await InsertVersionAsync(conn, docKeep, 1, "順序保持", """["旧タグ", "経理"]""");
        }

        // ── 移行を適用する ──────────────────────────────────────────────
        await using (var db = NewContext(cs))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(TargetMigration);
        }

        // ── 検証: EF が `List<Guid>` として読めること（＝形式が正しいこと）──
        await using (var db = NewContext(cs))
        {
            var tags = await db.Tags.ToDictionaryAsync(t => t.Name, t => t.Id);

            tags.Keys.Should().Contain(["経理", "規程", "旧タグ"],
                "現行版・版履歴の双方の表示名が辞書へ登録される");
            tags.Keys.Should().NotContain("",
                "空文字はタグではない");
            tags.Keys.Should().NotContain(n => n != n.Trim(),
                "登録される名前は正規化済みである（C# の Trim と同じ集合で落とす）");

            var keep = await db.Documents.SingleAsync(d => d.Id == docKeep);
            keep.Tags.Should().Equal([tags["経理"], tags["規程"], tags["経理"]],
                "並びも重複もそのまま保つ（並びは画面の表示順である）");

            var blank = await db.Documents.SingleAsync(d => d.Id == docBlank);
            blank.Tags.Should().BeEmpty(
                "解決できない要素しか無い行も表示名のまま取り残さない（LEFT JOIN + FILTER）");

            var empty = await db.Documents.SingleAsync(d => d.Id == docEmpty);
            empty.Tags.Should().BeEmpty();

            var whitespace = await db.Documents.SingleAsync(d => d.Id == docWhitespace);
            whitespace.Tags.Should().Equal([tags["経理"]],
                "全角空白で囲まれた名前も同じタグへ解決される");

            var version = await db.DocumentVersions.SingleAsync(v => v.DocumentId == docKeep);
            version.Tags.Should().Equal([tags["旧タグ"], tags["経理"]],
                "版履歴も識別子へ移る（過去版も新しい名前で表示されるための前提である）");

            // **辞書に既に在ったものは重複登録されない。**
            (await db.Tags.CountAsync(t => t.Name == "規程")).Should().Be(1);

            // 未改名のタグは `UpdatedAt` == `CreatedAt` である（`Tag.Create` と揃える）。
            // **scaffold の既定値 `0001-01-01` をそのまま使うと、未改名のタグが
            // 「西暦 1 年に改名された」ように見える。**
            var seeded = await db.Tags.SingleAsync(t => t.Name == "規程");
            seeded.UpdatedAt.Should().Be(seeded.CreatedAt);
        }
    }

    // **下りも検証する。** 巻き戻せないマイグレーションを「巻き戻せる」と称して置くと、
    // 障害時に初めて分かる。
    [DockerFact]
    public async Task Migration_Down_RestoresDisplayNames()
    {
        if (!postgres.IsAvailable) return;

        var cs = await CreateDatabaseAsync(postgres.ConnectionString!,
            $"tagmigdown_{Guid.NewGuid():N}");

        await using (var db = NewContext(cs))
            await db.GetService<IMigrator>().MigrateAsync(BeforeMigration);

        var docId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await InsertDocumentAsync(conn, docId, "往復", """["経理", "規程"]""");
            await InsertVersionAsync(conn, docId, 1, "往復", """["経理"]""");
        }

        await using (var db = NewContext(cs))
            await db.GetService<IMigrator>().MigrateAsync(TargetMigration);
        await using (var db = NewContext(cs))
            await db.GetService<IMigrator>().MigrateAsync(BeforeMigration);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            (await ScalarAsync(conn,
                $"""SELECT "Tags"::text FROM "Documents" WHERE "Id" = '{docId}';"""))
                .Should().Be("""["経理", "規程"]""",
                    "識別子は表示名へ戻る（並びも保つ）");
            (await ScalarAsync(conn,
                $"""SELECT "Tags"::text FROM "DocumentVersions" WHERE "DocumentId" = '{docId}';"""))
                .Should().Be("""["経理"]""");
        }
    }

    // 生 SQL の raw string 内で `{}` は補間の開き括弧になるため、定数で渡す。
    private const string EmptyAttributes = "{}";

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private static Task InsertDocumentAsync(NpgsqlConnection conn, Guid id, string title, string tagsJson) =>
        ExecAsync(conn, $"""
            INSERT INTO "Documents"
                ("Id", "Title", "Status", "Version", "Attributes", "Tags", "CreatedAt", "UpdatedAt")
            VALUES
                ('{id}', '{title}', 'draft', 1, '{EmptyAttributes}'::jsonb, '{tagsJson}'::jsonb, now(), now());
            """);

    private static Task InsertVersionAsync(NpgsqlConnection conn, Guid documentId, int version,
        string title, string tagsJson) =>
        ExecAsync(conn, $"""
            INSERT INTO "DocumentVersions"
                ("Id", "DocumentId", "Version", "Title", "Status", "Attributes", "Tags", "CreatedAt")
            VALUES
                ('{Guid.NewGuid()}', '{documentId}', {version}, '{title}', 'draft',
                 '{EmptyAttributes}'::jsonb, '{tagsJson}'::jsonb, now());
            """);
}
