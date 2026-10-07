using System.Data.Common;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
// 名前空間 Knowledge.IntegrationTests.DocumentService の末尾セグメントと衝突するため global:: で明示する。
using global::DocumentService.Domain;
using global::DocumentService.Features.Documents.ListPage;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-02, FR-06, NFR-08, IADR-0509 (#1765): 文書のキーセット（作成時刻昇順・同時刻は ID 昇順）を **SQL で**辿ったとき、
// 実 PostgreSQL で抜けも重複も無いことを固定する。`GET /documents/page`（`ReadPageAsync`）と再発行の口
// （`AfterCursor` + `InPageOrder` + `Take(limit + 1)` + `COUNT(*)`）は同じ 2 つの拡張を使う。
//
// **単体テストでは踏めない経路である。** `DocumentService.Tests` は EF InMemory であり、
// ① Postgres の uuid の順（16 バイトの比較）と .NET の `Guid.CompareTo` の順の食い違い、
// ② timestamptz のマイクロ秒への丸め（`DateTimeOffset` は 100 ns）、③ 索引が引かれるか、のいずれも測れない。
//
// 🔴 同時刻の群には、**先頭バイトの最上位ビットだけが違う Guid**・**後半 8 バイトだけが違う Guid**・
//   .NET の内部格納がリトルエンディアンになる先頭 3 フィールドの上位／下位バイトだけが違う Guid を置く。
[Trait("Category", "Integration")]
public sealed class DocumentKeysetPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 作成時刻はマイクロ秒ちょうどの値（timestamptz で丸まらない）。
    private static readonly DateTimeOffset T = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private static readonly Guid[] TieIds =
    [
        Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Guid.Parse("00000000-0000-0000-0000-000000000003"),
        Guid.Parse("00000000-0000-0000-0000-ff0000000000"), // 後半 8 バイトだけが違う
        Guid.Parse("00000000-0000-0000-8000-000000000000"), // 後半 8 バイトの先頭の最上位ビット
        Guid.Parse("00000000-0000-0001-0000-000000000000"), // _c の下位バイト
        Guid.Parse("00000000-0000-0100-0000-000000000000"), // _c の上位バイト
        Guid.Parse("00000000-0001-0000-0000-000000000000"), // _b の下位バイト
        Guid.Parse("00000000-0100-0000-0000-000000000000"), // _b の上位バイト
        Guid.Parse("000000ff-0000-0000-0000-000000000000"), // _a の下位バイト
        Guid.Parse("01000000-0000-0000-0000-000000000000"), // _a の上位バイト
        Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff"),
        Guid.Parse("80000000-0000-0000-0000-000000000000"), // 先頭バイトの最上位ビットだけが違う（符号の取り違えで前後が入れ替わる）
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
    ];

    private static readonly Dictionary<string, string> NoFilters = new(StringComparer.Ordinal);

    private static async Task<string> CreateDatabaseAsync(string baseConnectionString)
    {
        var name = $"dockeyset_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        builder.Database = name;
        // 本番と同じくマイグレーションで作る（索引のマイグレーションが当たることも見る）。
        await using (var db = NewContext(builder.ConnectionString))
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        return builder.ConnectionString;
    }

    private static DocumentDbContext NewContext(string connectionString, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>().UseNpgsql(connectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new DocumentDbContext(options.Options);
    }

    private static Dictionary<string, string> Org(string project) => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
        ["project"] = project,
    };

    private static async Task SeedAsync(string cs, params (Guid Id, DateTimeOffset CreatedAt, Dictionary<string, string> Attributes)[] rows)
    {
        await using var db = NewContext(cs);
        foreach (var (id, createdAt, attributes) in rows)
        {
            var doc = Document.CreateNormalized(id, $"doc-{id:N}", $"s3://bucket/{id:N}.md", attributes);
            db.Documents.Add(doc);
            db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = createdAt;
        }
        await db.SaveChangesAsync(Ct);
    }

    // 同時刻の群（13 件）＋ その直前・直後（1 マイクロ秒）＋ 個人資料と別プロジェクトの文書を同じ時刻に混ぜる。
    private static async Task<List<Guid>> SeedTieGroupAsync(string cs, string project)
    {
        var before = Guid.Parse("ffffffff-0000-0000-0000-000000000000");
        // 直前の行は群の大半より大きい ID、直後の行は群のどれより小さい ID にする（時刻が ID より先に効くことを見る）。
        // 🔴 `Guid.Empty` は使わない —— EF は鍵の既定値を「未設定」とみなして採番し直す。
        var after = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var rows = TieIds.Select(id => (id, T, Org(project))).ToList();
        rows.Add((before, T.AddTicks(-10), Org(project)));
        rows.Add((after, T.AddTicks(10), Org(project)));
        rows.Add((Guid.Parse("40000000-0000-0000-0000-000000000000"), T, new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = "alice",
            ["project"] = project,
        }));
        rows.Add((Guid.Parse("40000000-0000-0000-0000-000000000001"), T, Org("other")));
        await SeedAsync(cs, [.. rows]);
        // 期待の並び: 時刻の昇順、同時刻は .NET の `Guid.CompareTo` の昇順（従前の .NET の並びと同じ規則）。
        return [before, .. TieIds.OrderBy(id => id), after];
    }

    private static async Task<List<Guid>> WalkPagesAsync(string cs, IReadOnlyDictionary<string, string> filters, int limit)
    {
        var seen = new List<Guid>();
        DocumentPageCursor? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            await using var db = NewContext(cs);
            var (page, next) = await DocumentPageQuery.ReadPageAsync(
                db.Documents.AsNoTracking(), filters, limit, after, readable: null, Ct);
            page.Count.Should().BeLessThanOrEqualTo(limit);
            seen.AddRange(page.Select(d => d.Id));
            if (next is null) return seen;
            DocumentPageCursor.TryDecode(next, out var decoded).Should().BeTrue();
            after = decoded;
        }
        throw new InvalidOperationException("ページが終わらない（カーソルが進んでいない）");
    }

    // 再発行の口の形（属性の絞り込みなし）: `limit + 1` 行を読み、`COUNT(*)` で残りを数える。
    private static async Task<List<Guid>> WalkRepublishShapeAsync(string cs, int limit)
    {
        var seen = new List<Guid>();
        DocumentPageCursor? after = null;
        int? matched = null;
        for (var guard = 0; guard < 100; guard++)
        {
            await using var db = NewContext(cs);
            var query = db.Documents.AsNoTracking();
            var total = await query.CountAsync(Ct);
            matched ??= total;
            total.Should().Be(matched.Value, "matched はカーソルに依らない");
            var keyed = query.AfterCursor(after);
            var remaining = await keyed.CountAsync(Ct);
            remaining.Should().Be(matched.Value - seen.Count, "remaining はこの呼び出しの前に残っていた件数");
            var head = await keyed.InPageOrder().Take(limit + 1).Select(d => new { d.Id, d.CreatedAt }).ToListAsync(Ct);
            var page = head.Take(limit).ToList();
            seen.AddRange(page.Select(r => r.Id));
            if (head.Count <= limit) return seen;
            after = new DocumentPageCursor(page[^1].CreatedAt.UtcTicks, page[^1].Id);
        }
        throw new InvalidOperationException("ページが終わらない（カーソルが進んでいない）");
    }

    private async Task<string> DatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        postgres.IsAvailable.Should().BeTrue("門を通ったのに PostgreSQL を得られない");
        return await CreateDatabaseAsync(postgres.ConnectionString!);
    }

    // 🔴 Postgres の uuid の順は .NET の `Guid.CompareTo` の順と一致する（従前の .NET の並びから SQL の並びへ移しても、
    // 既存のカーソル〔.NET の規則で作った位置〕の意味が変わらない）。
    [Fact]
    public async Task SQLの並びは作成時刻とGuidCompareToの並びに一致する()
    {
        var cs = await DatabaseAsync();
        var project = $"p-{Guid.NewGuid():N}";
        await SeedTieGroupAsync(cs, project);

        await using var db = NewContext(cs);
        var all = await db.Documents.AsNoTracking().ToListAsync(Ct);
        var sql = await db.Documents.AsNoTracking().InPageOrder().Select(d => d.Id).ToListAsync(Ct);

        sql.Should().Equal(all.OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).Select(d => d.Id));
    }

    // FR-06: `GET /documents/page` の本体を 1・2・3・5・500 件ずつ辿り、同時刻の群の境目でも抜けも重複も無い。
    // 個人資料と別プロジェクトの文書（同じ時刻）は除かれ、塊の境目をまたいでも続きが見つかる。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(500)]
    public async Task ページの口は同時刻の群を抜けも重複もなく辿る(int limit)
    {
        var cs = await DatabaseAsync();
        var project = $"p-{Guid.NewGuid():N}";
        var expected = await SeedTieGroupAsync(cs, project);

        var seen = await WalkPagesAsync(cs, new Dictionary<string, string> { ["project"] = project }, limit);

        seen.Should().Equal(expected, "時刻の昇順、同時刻は Id の昇順で、ちょうど 1 回ずつ");
    }

    // FR-02: 再発行の口の形（`limit + 1` 行・`COUNT(*)`）で辿っても抜けも重複も無く、`remaining` が 1 ページずつ減る。
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(17)]
    public async Task 再発行の形は同時刻の群を抜けも重複もなく辿り残りを正しく数える(int limit)
    {
        var cs = await DatabaseAsync();
        var project = $"p-{Guid.NewGuid():N}";
        await SeedTieGroupAsync(cs, project);
        await using var db = NewContext(cs);
        var all = (await db.Documents.AsNoTracking().ToListAsync(Ct))
            .OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).Select(d => d.Id).ToList();

        var seen = await WalkRepublishShapeAsync(cs, limit);

        seen.Should().Equal(all, "再発行は個人資料も含めた全件を、ちょうど 1 回ずつ");
    }

    // 🔴 作成時刻の精度: `DateTimeOffset` は 100 ns、timestamptz はマイクロ秒。1 マイクロ秒の中で違う時刻を書くと、
    // DB では同時刻の群になる。カーソルは DB から読んだ値から作るので、群の境目で抜けも重複も無い。
    [Fact]
    public async Task マイクロ秒未満の差は丸まって同時刻の群になり_それでも抜けも重複もない()
    {
        var cs = await DatabaseAsync();
        var project = $"p-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        await SeedAsync(cs, [.. ids.Select((id, i) => (id, T.AddTicks(i + 1), Org(project)))]);

        await using (var db = NewContext(cs))
        {
            var stored = await db.Documents.AsNoTracking().Select(d => d.CreatedAt).Distinct().ToListAsync(Ct);
            stored.Should().HaveCountLessThan(ids.Count, "前提: 1 マイクロ秒の中の差は timestamptz で潰れる");
        }

        var expected = await ExpectedOrderAsync(cs);
        (await WalkPagesAsync(cs, NoFilters, 1)).Should().Equal(expected);
        (await WalkRepublishShapeAsync(cs, 2)).Should().Equal(expected);
    }

    // 境界: 空の台帳・最後のページがちょうど `limit` 件・カーソルが最後の行を指す。
    [Fact]
    public async Task 空の台帳_ちょうどlimit件の最後のページ_最後の行を指すカーソル()
    {
        var cs = await DatabaseAsync();

        await using (var db = NewContext(cs))
        {
            var (empty, next) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 5, null, null, Ct);
            empty.Should().BeEmpty();
            next.Should().BeNull();
        }

        var project = $"p-{Guid.NewGuid():N}";
        await SeedAsync(cs, [.. Enumerable.Range(0, 4).Select(i => (Guid.NewGuid(), T.AddTicks(10 * i), Org(project)))]);
        var expected = await ExpectedOrderAsync(cs);

        await using (var db = NewContext(cs))
        {
            var (page, next) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 4, null, null, Ct);
            page.Select(d => d.Id).Should().Equal(expected);
            next.Should().BeNull("ちょうど limit 件で尽きたら続きのカーソルは出さない");

            var (two, cursor) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 2, null, null, Ct);
            two.Select(d => d.Id).Should().Equal(expected.Take(2));
            DocumentPageCursor.TryDecode(cursor, out var mid).Should().BeTrue();
            var (rest, end) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 2, mid, null, Ct);
            rest.Select(d => d.Id).Should().Equal(expected.Skip(2));
            end.Should().BeNull("残りがちょうど limit 件の最後のページ");

            var last = await db.Documents.AsNoTracking().InPageOrder().LastAsync(Ct);
            var atLast = DocumentPageCursor.After(last);
            var (none, noNext) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 2, atLast, null, Ct);
            none.Should().BeEmpty("カーソルより厳密に後ろだけを返す");
            noNext.Should().BeNull();
            (await db.Documents.AsNoTracking().AfterCursor(atLast).CountAsync(Ct)).Should().Be(0);
        }
    }

    // 走査の途中の追加と削除（窓の両側）: 既読・未読の削除はカーソルを動かさず、途中で作られた文書は末尾に現れる。
    [Fact]
    public async Task 走査の途中の削除と追加でずっと在った文書を読み飛ばさない()
    {
        var cs = await DatabaseAsync();
        var project = $"p-{Guid.NewGuid():N}";
        await SeedAsync(cs, [.. Enumerable.Range(0, 6).Select(i => (Guid.NewGuid(), T.AddTicks(10 * (i / 2)), Org(project)))]);
        var ordered = await ExpectedOrderAsync(cs);

        DocumentPageCursor after;
        await using (var db = NewContext(cs))
        {
            var (first, next) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 2, null, null, Ct);
            first.Select(d => d.Id).Should().Equal(ordered.Take(2));
            DocumentPageCursor.TryDecode(next, out after).Should().BeTrue();

            // 既読の 2 件目（カーソルが指す行）と未読の 4 件目を消し、新しい文書を作る。
            await db.Documents.Where(d => d.Id == ordered[1] || d.Id == ordered[3]).ExecuteDeleteAsync(Ct);
        }
        var late = Guid.NewGuid();
        await SeedAsync(cs, (late, T.AddTicks(1_000), Org(project)));

        var rest = new List<Guid>();
        DocumentPageCursor? position = after;
        while (position is not null)
        {
            await using var db = NewContext(cs);
            var (page, next) = await DocumentPageQuery.ReadPageAsync(db.Documents.AsNoTracking(), NoFilters, 2, position, null, Ct);
            rest.AddRange(page.Select(d => d.Id));
            position = next is not null && DocumentPageCursor.TryDecode(next, out var c) ? c : null;
        }

        rest.Should().Equal([ordered[2], ordered[4], ordered[5], late]);
    }

    // 完了の条件: キーセットの問い合わせは `(CreatedAt, Id)` の索引を、**カーソルの位置から**引ける（マイグレーションが張った索引）。
    // 小さな表では計画器が逐次走査やビットマップ走査＋並べ替えを選ぶので、それらを禁じて「索引を並びのまま引ける形の SQL か」を見る。
    [Fact]
    public async Task キーセットの問い合わせはCreatedAtとIdの索引を引ける()
    {
        var cs = await DatabaseAsync();
        await SeedAsync(cs, [.. Enumerable.Range(0, 50).Select(i => (Guid.NewGuid(), T.AddTicks(10 * (i / 3)), Org("p")))]);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT indexdef FROM pg_indexes WHERE tablename = 'Documents' AND indexname = 'IX_Documents_CreatedAt_Id'", conn);
            ((string?)await cmd.ExecuteScalarAsync(Ct)).Should().Contain("(\"CreatedAt\", \"Id\")");
        }

        var capture = new CommandCapture();
        await using (var db = NewContext(cs, capture))
        {
            var mid = await db.Documents.AsNoTracking().InPageOrder().Skip(10).FirstAsync(Ct);
            capture.Last = null;
            _ = await db.Documents.AsNoTracking().AfterCursor(DocumentPageCursor.After(mid)).InPageOrder().Take(11).ToListAsync(Ct);
        }
        var captured = capture.Last ?? throw new InvalidOperationException("問い合わせを捕まえられなかった");
        captured.Text.Should().Contain("ORDER BY d.\"CreatedAt\", d.\"Id\"").And.Contain("LIMIT");

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await using (var off = new NpgsqlCommand("SET enable_seqscan = off; SET enable_bitmapscan = off", conn))
                await off.ExecuteNonQueryAsync(Ct);
            await using var explain = new NpgsqlCommand("EXPLAIN " + captured.Text, conn);
            foreach (var p in captured.Parameters) explain.Parameters.Add(p);
            var plan = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) plan.Add(reader.GetString(0));
            var text = string.Join("\n", plan);
            text.Should().Contain("Index Scan using \"IX_Documents_CreatedAt_Id\"", text);
            // 🔴 カーソルの位置が索引の**開始位置**（Index Cond）に入っている。素の OR の形
            // （`CreatedAt > c OR (CreatedAt = c AND Id > id)`）だと Filter にしか現れず、索引を先頭からなめる（位置に比例して読む）。
            text.Should().MatchRegex(@"Index Cond: \(""CreatedAt"" >= ", text);
        }
    }

    private static async Task<List<Guid>> ExpectedOrderAsync(string cs)
    {
        await using var db = NewContext(cs);
        return [.. (await db.Documents.AsNoTracking().ToListAsync(Ct))
            .OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).Select(d => d.Id)];
    }

    // EF が送った SQL と引数を写し取る（EXPLAIN に同じ文を渡すため）。
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public (string Text, List<NpgsqlParameter> Parameters)? Last { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Last = (command.CommandText, [.. command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone())]);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
