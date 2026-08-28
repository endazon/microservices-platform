using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.GraphService;

// FR-17, SC-09, ADR-0033 決定 5・6・9, IADR-0242 決定 7 / #941:
// **辺の型辞書の DB 層の防壁が「機能したこと」を確認する。**
//
// 🔴 **これは「DB 層のテストを足す」ものではない。** 防壁（`ON DELETE RESTRICT` /
// `ux_edge_types_name` / `ux_edges`）は 2026-08-22 の InitialCreate から宣言されているが、
// **一度も発火したことがない**。#910 の変異試験 G-1 で、アプリ層の事前カウントを外すと
// 「RESTRICT に弾かれて 500」ではなく **204 NoContent で参照中の型が黙って消えた**。
// 単体テスト（`GraphService.Tests`）は EF InMemory を使っており、
// **InMemory プロバイダは一意索引も外部キーも強制しない**ためである（実測: 同プロジェクトの
// `UseNpgsql` は 0 件）。したがってこの層は**実 PostgreSQL でしか測れない**。
//
// 🔴 **`EnsureCreatedAsync` を呼ばない。** `DocumentService` 側の同型のテストは呼んでいるが、
// それはモデルから直接スキーマを作るので「**マイグレーションが RESTRICT を正しく出力しているか**」
// を測れない（#941 の指摘そのもの）。GraphService の `Program.cs` は起動時に `MigrateAsync` を
// 実行するため、ホストを起こすだけでスキーマはマイグレーション出力になる。
//
// 🔴 **防壁は HTTP ではなく `GraphDbContext` を直接叩いて確かめる。** アプリ層のガードを外す変異を
// 入れて確かめると「変異を入れた版」しか試験できず、出荷される版の防壁は未発火のまま残る。
[Trait("Category", "Integration")]
public sealed class EdgeTypeDbGuardTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private GraphServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable) return ValueTask.CompletedTask;
        _factory = new GraphServiceFactory(postgres);
        // CreateClient がホストを起こし、Program.cs の MigrateAsync ＋ 型の初期値集合の投入が走る。
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    // ── 検証 1: ON DELETE RESTRICT ────────────────────────────────

    // FR-17, SC-09, ADR-0033 決定 9: 参照が 1 件でもある辺の型は、**DB 層の ON DELETE RESTRICT が
    // 削除を拒む**。アプリ層の事前カウントを通らない経路（DbContext 直接）で確かめる ——
    // 変異試験 G-1 が「アプリ層を外すと黙って消える」ことを実測した、その最後の防壁である。
    //
    // 🔴 **削除は「辺を 1 件も読み込んでいない DbContext」から行わなければならない。**
    // 型と辺を同じ DbContext で作ってから `Remove` すると、**DELETE 文が 1 度も発行されない**。
    // EF Core のカスケード／切断の挙動は**変更追跡下の実体にだけ**適用され、既定の
    // `CascadeTiming.Immediate` では `Remove()` の呼び出し中にその判定が走る。`Restrict` は
    // 追跡中の依存側に対して何もしないため、依存側の非 null 外部キーが削除済みの主体を指したまま
    // 「概念上の null」になり、EF は SQL を組み立てる前に同期的に `InvalidOperationException`
    //（"The association between entity types 'EdgeType' and 'Edge' has been severed…"）を投げる。
    // **例外は出るが PostgreSQL には一度も届かない** —— 防壁を確かめたつもりで何も確かめていない、
    // 本 issue が正そうとしている当の形である（実測: 初版がこの形で fail した）。
    //
    // 依存側を読み込んでいなければ EF は何も切断せず、DELETE 文がそのまま発行されて
    // データベース側の参照アクションが効く。**本番の削除経路と同じ形でもある** ——
    // エンドポイントは型だけを読み、使用件数はスカラの COUNT で数えるので辺を 1 件も追跡しない。
    [Fact]
    public async Task 参照中の辺の型は削除がDBのRESTRICTで拒まれる()
    {
        DockerRequired.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // (1) 型と、それを参照する辺を作る。ここで作った DbContext は捨てる。
        Guid typeId;
        await using (var seeding = _factory.Services.CreateAsyncScope())
        {
            var db = seeding.ServiceProvider.GetRequiredService<GraphDbContext>();
            var type = EdgeType.Create($"restrict-{Guid.NewGuid():N}", EdgeTypeLayer.Core, isSymmetric: false);
            db.EdgeTypes.Add(type);
            await db.SaveChangesAsync(ct);
            typeId = type.Id;

            db.Edges.Add(Edge.Create(Guid.NewGuid(), Guid.NewGuid(), typeId,
                isSymmetric: false, EdgeProvenance.Auto));
            await db.SaveChangesAsync(ct);
        }

        // (2) **辺を 1 件も読み込んでいない**別の DbContext から削除する。
        await using (var deleting = _factory.Services.CreateAsyncScope())
        {
            var db = deleting.ServiceProvider.GetRequiredService<GraphDbContext>();
            var type = await db.EdgeTypes.FirstAsync(t => t.Id == typeId, ct);

            // 🔴 **この不変条件が破れると、この試験は黙って DB へ届かなくなる。**
            // 辺を 1 件でも追跡した状態で Remove すると、上の注記のとおり EF が
            // クライアント側で例外を投げ、DELETE 文が発行されない。ここで先に落とす。
            db.ChangeTracker.Entries<Edge>().Should().BeEmpty(
                "辺を追跡していると EF が Remove の時点で例外を投げ、PostgreSQL の RESTRICT に到達しない");

            db.EdgeTypes.Remove(type);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
            var pg = ex.InnerException.Should().BeOfType<PostgresException>(
                "RESTRICT の拒否は PostgreSQL の外部キー違反として現れる").Subject;
            pg.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
            pg.ConstraintName.Should().Be("FK_edges_edge_types_EdgeTypeId");
        }

        // (3) **型の行は残っている。** 「例外は出たが実は消えていた」を排除する。
        await using (var verify = _factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<GraphDbContext>();
            (await db.EdgeTypes.AsNoTracking().AnyAsync(t => t.Id == typeId, ct))
                .Should().BeTrue("拒否されたのだから行は残っていなければならない");
        }
    }

    // ── 検証 2: ux_edge_types_name ────────────────────────────────

    // FR-17, SC-09, ADR-0033 決定 3: 「新しい名前は既存値と重複しない」。
    // **アプリ層の事前検査（ExistsAsync）を通らない経路でも同名は 2 件入らない。**
    [Fact]
    public async Task 同名の辺の型は一意索引で2件目が拒まれる()
    {
        DockerRequired.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var name = $"dup-{Guid.NewGuid():N}";

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();

        db.EdgeTypes.Add(EdgeType.Create(name, EdgeTypeLayer.Core, isSymmetric: false));
        await db.SaveChangesAsync(ct);

        db.EdgeTypes.Add(EdgeType.Create(name, EdgeTypeLayer.Core, isSymmetric: false));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        var pg = ex.InnerException.Should().BeOfType<PostgresException>().Subject;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("ux_edge_types_name");
    }

    // ── 検証 3: ux_edges とアンカーの空文字既定 ────────────────────

    // FR-17, ADR-0033 決定 5・6: 同一の (始点, 終点, 型, 始点アンカー, 終点アンカー) は 2 行入らない。
    // 🔴 **アンカー列を空文字既定にした判断が実際に効いているかを、ここで初めて確かめる** ——
    // NULL 可なら PostgreSQL は NULL 同士を相異なるものとして扱い、この一意制約は素通りする。
    [Fact]
    public async Task 同一の関係を表す辺は一意索引で2行目が拒まれる()
    {
        DockerRequired.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();

        var type = EdgeType.Create($"uxedges-{Guid.NewGuid():N}", EdgeTypeLayer.Core, isSymmetric: false);
        db.EdgeTypes.Add(type);
        await db.SaveChangesAsync(ct);

        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        // アンカーを指定しない＝文書単位。Edge.Create が null を空文字へ正規化する。
        var first = Edge.Create(source, target, type.Id, isSymmetric: false, EdgeProvenance.Auto);
        db.Edges.Add(first);
        await db.SaveChangesAsync(ct);

        // **保存された値が NULL ではなく空文字であること**（一意制約が効く前提そのもの）。
        var stored = await db.Edges.AsNoTracking().FirstAsync(e => e.Id == first.Id, ct);
        stored.SourceAnchor.Should().BeEmpty();
        stored.TargetAnchor.Should().BeEmpty();

        db.Edges.Add(Edge.Create(source, target, type.Id, isSymmetric: false, EdgeProvenance.User));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        var pg = ex.InnerException.Should().BeOfType<PostgresException>().Subject;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("ux_edges");
    }

    // ── 検証 4: 一意制約違反 → 409 変換 ───────────────────────────

    // FR-17, SC-09: **同名の同時登録でも 500 にならない。** 事前検査（ExistsAsync）と保存の間に
    // 別の要求が同名を入れられるため、事前検査だけでは埋まらない。一意制約違反を
    // `DbUpdateException` として捕まえて 409 へ写す分岐は、**一意制約が効かないと 1 度も通らない。**
    [Fact]
    public async Task 同名を同時に登録しても1件だけ成功し500にならない()
    {
        DockerRequired.SkipUnlessAvailable();
        // ラムダの中で TestContext.Current を引かない（並列タスクでは AsyncLocal の値が保証されない）。
        var ct = TestContext.Current.CancellationToken;
        var name = $"競合-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => _client.PostAsJsonAsync(
                "/graph/edge-types", new CreateEdgeTypeRequest(name, EdgeTypeLayer.Core, false), ct)));

        try
        {
            responses.Should().OnlyContain(
                r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict,
                "重複は 409 が契約であり、素の 500 にしてはならない");
            responses.Count(r => r.StatusCode == HttpStatusCode.Created)
                .Should().Be(1, "ux_edge_types_name が 1 件だけを通す");
        }
        finally
        {
            foreach (var r in responses) r.Dispose();
        }
    }

    // FR-17, SC-09, ADR-0033 決定 9: **RESTRICT に弾かれた削除も 409 で返す**（素の 500 で漏らさない）。
    //
    // 競合を**決定的に**再現する。別接続の未コミットトランザクションで辺を挿入しておくと、
    //   1. 削除要求の事前カウントは未コミット行を見ないので 0 件（＝アプリ層のガードを通過する）
    //   2. `DELETE FROM edge_types` は親行のロック待ちで**ブロックする**
    //   3. こちらが commit すると、削除は外部キー違反で失敗する
    // という順序が保証される。#910 が塞いだ「事前カウントと削除の間に辺が張られた」経路そのものである。
    [Fact]
    public async Task RESTRICTに弾かれた削除は409になる()
    {
        DockerRequired.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        Guid typeId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
            var type = EdgeType.Create($"race-{Guid.NewGuid():N}", EdgeTypeLayer.Core, isSymmetric: false);
            db.EdgeTypes.Add(type);
            await db.SaveChangesAsync(ct);
            typeId = type.Id;
        }

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO edges ("Id", "SourceDocumentId", "TargetDocumentId", "EdgeTypeId",
                               "Provenance", "SourceAnchor", "TargetAnchor", "CreatedAt", "UpdatedAt")
            VALUES (@id, @s, @t, @ty, 'auto', '', '', now(), now())
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("s", Guid.NewGuid());
            insert.Parameters.AddWithValue("t", Guid.NewGuid());
            insert.Parameters.AddWithValue("ty", typeId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // 削除要求を先に走らせる。事前カウントは 0 を見て通過し、DELETE で親行のロック待ちに入る。
        var deleting = _client.DeleteAsync($"/graph/edge-types/{typeId}", ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await tx.CommitAsync(ct);

        using var res = await deleting;
        // 🔴 **どちらの層が捕まえても契約は 409 である。** 500 で漏らさないことがこの試験の主眼。
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<EdgeTypeInUseResponse>(ct);
        body.Should().NotBeNull();
        body!.Error.Should().Be("edge_type_in_use");
        body.UsageCount.Should().Be(1);

        await using var verify = _factory.Services.CreateAsyncScope();
        var check = verify.ServiceProvider.GetRequiredService<GraphDbContext>();
        (await check.EdgeTypes.AsNoTracking().AnyAsync(t => t.Id == typeId, ct))
            .Should().BeTrue("409 を返したのだから型は残っていなければならない");
    }

    // ── 検証 5: マイグレーション出力そのものの突合 ─────────────────

    // FR-17, ADR-0033 決定 5・6・9: **マイグレーションが出力したスキーマをカタログで突き合わせる。**
    //
    // 🔴 **「張っていない外部キー」は書き込みでは反証できない。** `ai_suggestions` → `edge_types` と
    // `edges` → `graph_documents` に FK を張らないのは意図した設計であり（前者は決定 9 より厳しい規則を
    // 作らないため、後者はイベント到着順への依存を作らないため）、**後から誰かが張っても書き込み試験は
    // 緑のまま**である。カタログを見る以外に固定する手段が無い。
    [Fact]
    public async Task マイグレーションが出力したスキーマがカタログと一致する()
    {
        DockerRequired.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync(ct);

        // (1) グラフ 4 表の外部キーは **`edges` の 1 本だけ**で、削除規則は RESTRICT（confdeltype='r'）。
        var fks = await QueryAsync(conn, ct,
            """
            SELECT t.relname::text || '|' || c.conname::text || '|' || c.confdeltype::text
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE c.contype = 'f' AND n.nspname = 'public'
              AND t.relname IN ('edges', 'edge_types', 'graph_documents', 'ai_suggestions')
            """);
        fks.Should().BeEquivalentTo(["edges|FK_edges_edge_types_EdgeTypeId|r"],
            "FK は edges → edge_types の 1 本だけであり、削除規則は RESTRICT（r）でなければならない");

        // (2) 一意索引が UNIQUE として実在する。
        var uniques = await QueryAsync(conn, ct,
            """
            SELECT indexname::text FROM pg_indexes
            WHERE schemaname = 'public' AND tablename IN ('edges', 'edge_types')
              AND indexdef LIKE 'CREATE UNIQUE INDEX%'
              AND indexname IN ('ux_edges', 'ux_edge_types_name')
            """);
        uniques.Should().BeEquivalentTo(["ux_edge_types_name", "ux_edges"]);

        // (3) `ux_edges` が並べる 5 列（順序込み）。列が欠けても UNIQUE には見えるため、定義を見る。
        var uxEdgesDef = await QueryAsync(conn, ct,
            "SELECT indexdef::text FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ux_edges'");
        uxEdgesDef.Should().ContainSingle().Which.Should().Contain(
            """("SourceDocumentId", "TargetDocumentId", "EdgeTypeId", "SourceAnchor", "TargetAnchor")""");

        // (4) アンカー列は NOT NULL ＋ 既定が空文字。**NULL 可なら (3) の一意制約は素通りする。**
        var anchors = await QueryAsync(conn, ct,
            """
            SELECT column_name::text || '|' || is_nullable::text
                   || '|' || coalesce(column_default::text, 'NULL')
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'edges'
              AND column_name IN ('SourceAnchor', 'TargetAnchor')
            """);
        anchors.Should().HaveCount(2);
        anchors.Should().OnlyContain(a => a.Contains("|NO|"), "NULL 可にすると ux_edges が壊れる");
        anchors.Should().OnlyContain(a => a.Contains("''::character varying"), "既定は空文字である");
    }

    private static async Task<List<string>> QueryAsync(
        NpgsqlConnection conn, CancellationToken ct, string sql)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(reader.GetString(0));
        return rows;
    }
}
