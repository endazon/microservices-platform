using System.Data.Common;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Platform.Shared.Contracts.Dtos;
// 名前空間 Knowledge.IntegrationTests.DocumentService の末尾セグメントと衝突するため global:: で明示する。
using global::DocumentService.Domain;
using global::DocumentService.Domain.Ports;
using global::DocumentService.Features.Documents;
using global::DocumentService.Features.Documents.ContentAbac;
using global::DocumentService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-06, NFR-16, IADR-0537 (#1923・#1897): gRPC `ListDocuments` のページ（更新日時の降順・同時刻は ID の降順のキーセット）を
// **実 PostgreSQL で**辿ったとき、抜けも重複も無く、`(UpdatedAt, Id)` の索引を位置から引けることを固定する。
//
// **単体テスト（EF InMemory）では踏めない経路である**（`DocumentKeysetPostgresTests` と同じ理由）——
// ① Postgres の uuid の順と .NET の `Guid.CompareTo` の順、② timestamptz のマイクロ秒への丸め、③ 索引が引かれるか。
// 同時刻の群には、`DocumentKeysetPostgresTests` と同じく、バイト順の取り違えで前後が入れ替わる Guid を置く。
[Trait("Category", "Integration")]
public sealed class DocumentListKeysetPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 更新時刻はマイクロ秒ちょうどの値（timestamptz で丸まらない）。
    private static readonly DateTimeOffset T = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private static readonly Guid[] TieIds =
    [
        Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Guid.Parse("00000000-0000-0000-0000-ff0000000000"),
        Guid.Parse("00000000-0000-0000-8000-000000000000"),
        Guid.Parse("00000000-0000-0001-0000-000000000000"),
        Guid.Parse("00000000-0000-0100-0000-000000000000"),
        Guid.Parse("00000000-0001-0000-0000-000000000000"),
        Guid.Parse("00000000-0100-0000-0000-000000000000"),
        Guid.Parse("000000ff-0000-0000-0000-000000000000"),
        Guid.Parse("01000000-0000-0000-0000-000000000000"),
        Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff"),
        Guid.Parse("80000000-0000-0000-0000-000000000000"),
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
    ];

    private static readonly DocumentReadPrincipal Machine = DocumentReadPrincipal.RelayedUser("service-account-bff");

    private async Task<string> DatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        postgres.IsAvailable.Should().BeTrue("門を通ったのに PostgreSQL を得られない");

        var name = $"doclist_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString!);
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString!))
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

    // 内容の ABAC の門は閉じたまま（本番の既定）。門が閉じていれば組織文書の判定で認可サービスを問わない。
    private static DocumentReadUseCase Reads(DocumentDbContext db)
        => new(db, new DocumentReadAccess(new NoScopes(), new ClosedGate()));

    private static Dictionary<string, string> Org() => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
    };

    private static async Task SeedAsync(string cs, params (Guid Id, DateTimeOffset UpdatedAt, Dictionary<string, string> Attributes)[] rows)
    {
        await using var db = NewContext(cs);
        foreach (var (id, updatedAt, attributes) in rows)
        {
            var doc = Document.CreateNormalized(id, $"doc-{id:N}", $"s3://bucket/{id:N}.md", attributes);
            db.Documents.Add(doc);
            db.Entry(doc).Property(d => d.UpdatedAt).CurrentValue = updatedAt;
        }
        await db.SaveChangesAsync(Ct);
    }

    // 同時刻の群（12 件）＋ その直前・直後（1 マイクロ秒）＋ 同じ時刻の他人の個人資料（読めない）。
    private static async Task<List<Guid>> SeedTieGroupAsync(string cs)
    {
        // 新しい側の行は群のどれより小さい ID、古い側の行は群の大半より大きい ID（時刻が ID より先に効くことを見る）。
        var newer = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var older = Guid.Parse("ffffffff-0000-0000-0000-000000000000");
        var rows = TieIds.Select(id => (id, T, Org())).ToList();
        rows.Add((newer, T.AddTicks(10), Org()));
        rows.Add((older, T.AddTicks(-10), Org()));
        rows.Add((Guid.Parse("40000000-0000-0000-0000-000000000000"), T, new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["doc_scope"] = "private-note",
            ["owner"] = "alice",
        }));
        await SeedAsync(cs, [.. rows]);
        // 期待の並び: 時刻の降順、同時刻は .NET の `Guid.CompareTo` の降順（Postgres の uuid の順と一致する。IADR-0509）。
        return [newer, .. TieIds.OrderByDescending(id => id), older];
    }

    private static async Task<List<Guid>> WalkAsync(string cs, int pageSize)
    {
        var seen = new List<Guid>();
        DocumentListCursor? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            await using var db = NewContext(cs);
            var (page, next) = await Reads(db).ListPageAsync(Machine, pageSize, after, Ct);
            page.Count.Should().BeLessThanOrEqualTo(pageSize);
            seen.AddRange(page.Select(d => d.Id));
            if (next is null) return seen;
            DocumentListCursor.TryDecode(next, out var decoded).Should().BeTrue();
            after = decoded;
        }
        throw new InvalidOperationException("ページが終わらない（印が進んでいない）");
    }

    // 1・2・5・500 件ずつ辿り、同時刻の群の境目でも抜けも重複も無い。読めない個人資料は除かれ、塊の境目をまたいでも続きが見つかる。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(500)]
    public async Task 一覧のページは同時刻の群を抜けも重複もなく辿る(int pageSize)
    {
        var cs = await DatabaseAsync();
        var expected = await SeedTieGroupAsync(cs);

        var seen = await WalkAsync(cs, pageSize);

        seen.Should().Equal(expected, "更新時刻の降順、同時刻は Id の降順で、ちょうど 1 回ずつ");
    }

    // 🔴 更新時刻の精度: 1 マイクロ秒の中で違う時刻を書くと DB では同時刻の群になる。印は DB から読んだ値から作るので抜けない。
    [Fact]
    public async Task マイクロ秒未満の差は同時刻の群になり_それでも抜けも重複もない()
    {
        var cs = await DatabaseAsync();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        await SeedAsync(cs, [.. ids.Select((id, i) => (id, T.AddTicks(i + 1), Org()))]);

        await using (var db = NewContext(cs))
        {
            var stored = await db.Documents.AsNoTracking().Select(d => d.UpdatedAt).Distinct().ToListAsync(Ct);
            stored.Should().HaveCountLessThan(ids.Count, "前提: 1 マイクロ秒の中の差は timestamptz で潰れる");
        }

        List<Guid> expected;
        await using (var db = NewContext(cs))
            expected = [.. (await db.Documents.AsNoTracking().ToListAsync(Ct))
                .OrderByDescending(d => d.UpdatedAt.UtcTicks).ThenByDescending(d => d.Id).Select(d => d.Id)];

        (await WalkAsync(cs, 1)).Should().Equal(expected);
    }

    // 完了の条件: キーセットの問い合わせは `(UpdatedAt, Id)` の索引を**後ろから**、**印の位置から**引ける（マイグレーションが張った索引）。
    // 小さな表では計画器が逐次走査を選ぶので、それを禁じて「索引を並びのまま引ける形の SQL か」を見る。
    [Fact]
    public async Task 一覧のキーセットはUpdatedAtとIdの索引を後ろから引ける()
    {
        var cs = await DatabaseAsync();
        await SeedAsync(cs, [.. Enumerable.Range(0, 50).Select(i => (Guid.NewGuid(), T.AddTicks(10 * (i / 3)), Org()))]);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT indexdef FROM pg_indexes WHERE tablename = 'Documents' AND indexname = 'IX_Documents_UpdatedAt_Id'", conn);
            ((string?)await cmd.ExecuteScalarAsync(Ct)).Should().Contain("(\"UpdatedAt\", \"Id\")");
        }

        var capture = new CommandCapture();
        await using (var db = NewContext(cs, capture))
        {
            var mid = await db.Documents.AsNoTracking().InListOrder().Skip(10).FirstAsync(Ct);
            capture.Last = null;
            _ = await db.Documents.AsNoTracking().Before(DocumentListCursor.After(mid)).InListOrder().Take(11).ToListAsync(Ct);
        }
        var captured = capture.Last ?? throw new InvalidOperationException("問い合わせを捕まえられなかった");
        captured.Text.Should().Contain("ORDER BY d.\"UpdatedAt\" DESC, d.\"Id\" DESC").And.Contain("LIMIT");

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
            text.Should().Contain("Index Scan Backward using \"IX_Documents_UpdatedAt_Id\"", text);
            // 🔴 印の位置が索引の**開始位置**（Index Cond）に入っている（`DocumentListOrder.Before` の先頭の項）。
            text.Should().MatchRegex(@"Index Cond: \(""UpdatedAt"" <= ", text);
        }
    }

    // 内容の ABAC の門が閉じている間、組織文書と他人の個人資料の判定は認可サービスを問わない（呼ばれたら試験の誤り）。
    private sealed class NoScopes : IDocumentReadScopeSource
    {
        public Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveReadBranchesAsync(string userId, CancellationToken ct)
            => throw new InvalidOperationException("門が閉じている間の一覧は認可サービスを問わない");
    }

    private sealed class ClosedGate : IContentAbacGate
    {
        public bool IsOpen => false;
        public ContentAbacGateState State => ContentAbacGateState.Disabled;
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
