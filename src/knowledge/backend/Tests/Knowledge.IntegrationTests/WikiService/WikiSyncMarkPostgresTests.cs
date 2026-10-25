using System.Collections.Concurrent;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using global::WikiService.Common.Observability;
using global::WikiService.Domain;
using global::WikiService.Domain.Ports;
using global::WikiService.Features.Wiki;
using global::WikiService.Features.Wiki.RemoveDeleted;
using global::WikiService.Features.Wiki.SyncDocument;
using global::WikiService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.WikiService;

// FR-13, FR-06, [[IADR-0596]] 決定 3 (#2102): **同じ文書の版の印を 2 者が同時に作る**経路を実 PostgreSQL で通す。
//
// 単体試験（`WikiSyncOrderingTests`）は EF InMemory で、既存の印の改訂番号が動いた場合（DbUpdateConcurrencyException）しか踏めない。
// 初回の同期（印がまだ無い）で 2 つの受け口が並ぶと、後から保存する側は主キー `PK_SyncMarks` の一意違反（23505）で負ける。
// その例外が `WikiSyncOrdering.IsMarkConflict` に捕まり、古い側が保存済みの最新の状態へ Wiki.js を合わせ直すことを、
// 本番と同じマイグレーションで作った表で確かめる。
[Trait("Category", "Integration")]
public sealed class WikiSyncMarkPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T1 = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(1);
    private static readonly DateTimeOffset T3 = T1.AddMinutes(2);

    private static async Task<string> CreateDatabaseAsync(string baseConnectionString)
    {
        var name = $"wikimarks_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        builder.Database = name;
        // 本番と同じくマイグレーションで作る（主キーの名前 `PK_SyncMarks` がマイグレーションの値であることも見る）。
        await using (var db = NewContext(builder.ConnectionString))
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        return builder.ConnectionString;
    }

    private static WikiDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<WikiDbContext>().UseNpgsql(connectionString).Options);

    private static readonly ConsumerCallTimeouts Calls = new ServiceCollection()
        .AddLogging().AddPlatformConsumerTimeouts().BuildServiceProvider().GetRequiredService<ConsumerCallTimeouts>();

    private static readonly WikiJsWriteGate Gate = new(2); // 受け口の中で別の受け口を走らせるので枠を 2 にする

    private static async Task SyncAsync(string cs, StateWikiJs wiki, DocumentUpdated ev)
    {
        await using var db = NewContext(cs);
        var metrics = new WikiSyncSkippedMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        await new DocumentSyncConsumer(db, wiki, wiki, Calls, WikiSyncTimeouts.Default, Gate, metrics,
                NullLogger<DocumentSyncConsumer>.Instance)
            .Handle(ev, Ct);
    }

    private static async Task DeleteAsync(string cs, StateWikiJs wiki, Guid documentId, DateTimeOffset at)
    {
        await using var db = NewContext(cs);
        await new DocumentDeletedConsumer(db, wiki, wiki, Calls, WikiSyncTimeouts.Default, Gate,
                NullLogger<DocumentDeletedConsumer>.Instance)
            .Handle(new DocumentDeleted(documentId, at), Ct);
    }

    private static DocumentUpdated Updated(Guid id, string title, DateTimeOffset at, string confidentiality)
        => new(id, title, "published", $"storage://b/{id:N}/document.md",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality }, ["ops"], at);

    // 2 者が同時に印を作ると、後の保存は 23505（`PK_SyncMarks`）で負け、`IsMarkConflict` がそれを突き合わせの負けと読む。
    [Fact]
    public async Task 印の同時作成は主キーの一意違反になり突き合わせの負けと読む()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = Guid.NewGuid();

        await using (var first = NewContext(cs))
        {
            var mark = DocumentSyncMark.For(id);
            mark.RecordUpdated(T1, null);
            first.SyncMarks.Add(mark);
            await first.SaveChangesAsync(Ct);
        }

        await using var second = NewContext(cs);
        var dup = DocumentSyncMark.For(id);
        dup.RecordUpdated(T2, null);
        second.SyncMarks.Add(dup);
        var act = () => second.SaveChangesAsync(Ct);
        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;

        WikiSyncOrdering.IsMarkConflict(ex).Should().BeTrue();
        ex.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(WikiDbContext.SyncMarkKeyName);
    }

    // 初回の同期: 古い版（T1）の push の最中に新しい版（T2）が push と保存を終える。古い版の印の作成は 23505 で負け、
    // 保存済みの新しい版の状態へ Wiki.js を合わせ直す（古い版の push を残さない）。
    [Fact]
    public async Task 初回の同期で印の作成に負けた古い版は新しい版の状態へ合わせ直す()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = Guid.NewGuid();
        var wiki = new StateWikiJs();

        wiki.BeforeNextUpsert = () => SyncAsync(cs, wiki, Updated(id, "新しい版", T2, "confidential"));
        await SyncAsync(cs, wiki, Updated(id, "古い版", T1, "public"));

        wiki.State[WikiPage.PathFor(id)].Should().Be(("新しい版", true));
        await using var db = NewContext(cs);
        var page = await db.Pages.AsNoTracking().SingleAsync(p => p.DocumentId == id, Ct);
        page.Title.Should().Be("新しい版");
        page.Attributes["confidentiality"].Should().Be("confidential");
        (await db.SyncMarks.AsNoTracking().SingleAsync(m => m.DocumentId == id, Ct)).AppliedAt.Should().Be(T2);
    }

    // 初回の同期と削除が並ぶ: 同期（T1）の push の最中に削除（T3）が終わる。同期の印の作成は 23505 で負け、ページを消し直す。
    [Fact]
    public async Task 初回の同期で削除に印の作成を負けた古い版はページを消し直す()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = Guid.NewGuid();
        var wiki = new StateWikiJs();

        wiki.BeforeNextUpsert = () => DeleteAsync(cs, wiki, id, T3);
        await SyncAsync(cs, wiki, Updated(id, "古い版", T1, "internal"));

        wiki.State.Should().NotContainKey(WikiPage.PathFor(id));
        await using var db = NewContext(cs);
        (await db.Pages.AsNoTracking().AnyAsync(p => p.DocumentId == id, Ct)).Should().BeFalse();
        var mark = await db.SyncMarks.AsNoTracking().SingleAsync(m => m.DocumentId == id, Ct);
        mark.Deleted.Should().BeTrue();
    }

    // 🔴 FR-13, IADR-0639 決定 4 (#2150 / #2251) 段 2: **印の連番（`AppliedSequence`）を実 PostgreSQL で測る**（migration 済みの列・
    // timestamptz のマイクロ秒への丸め）。時刻はマイクロ秒より細かい端数つき —— 丸めを外すと、同じ時刻の古い版を端数の分だけ新しいと読む。
    // 規則 11 の表の P1（同時刻の逆転）と P3（撤去の後の古い ON）・P2（作り直し）。
    private static readonly DateTimeOffset Ts = T1.AddTicks(3);

    private static DocumentUpdated Sequenced(Guid id, string title, DateTimeOffset at, long sequence, bool exposed = true)
    {
        var ev = Updated(id, title, at, "internal") with { Sequence = sequence };
        return exposed
            ? ev
            : ev with { Attributes = DocumentExposure.WithAllExcluded(ev.Attributes) };
    }

    [Fact]
    public async Task P1とP3_同じ時刻で連番の小さい古い版は反映せず_印に連番が残る()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = Guid.NewGuid();
        var wiki = new StateWikiJs();

        await SyncAsync(cs, wiki, Sequenced(id, "新しい題名", Ts, 6));
        await SyncAsync(cs, wiki, Sequenced(id, "古い題名", Ts, 5));

        await using (var db = NewContext(cs))
        {
            (await db.Pages.AsNoTracking().SingleAsync(p => p.DocumentId == id, Ct)).Title.Should().Be("新しい題名", "P1");
            (await db.SyncMarks.AsNoTracking().SingleAsync(m => m.DocumentId == id, Ct)).AppliedSequence.Should().Be(6);
        }

        await SyncAsync(cs, wiki, Sequenced(id, "撤去", Ts, 7, exposed: false));
        await SyncAsync(cs, wiki, Sequenced(id, "古い ON", Ts, 6));

        await using (var db = NewContext(cs))
            (await db.Pages.AsNoTracking().AnyAsync(p => p.DocumentId == id, Ct)).Should().BeFalse("P3: 撤去の後の古い ON は載り直さない（#2251）");
    }

    [Fact]
    public async Task P2_削除の後に同じIDで作り直された文書は連番が小さくても反映する()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var cs = await CreateDatabaseAsync(postgres.ConnectionString!);
        var id = Guid.NewGuid();
        var wiki = new StateWikiJs();

        await SyncAsync(cs, wiki, Sequenced(id, "削除前", Ts, 8));
        await using (var db = NewContext(cs))
            await new DocumentDeletedConsumer(db, wiki, wiki, Calls, WikiSyncTimeouts.Default, Gate,
                    NullLogger<DocumentDeletedConsumer>.Instance)
                .Handle(new DocumentDeleted(id, Ts.AddSeconds(1), 9), Ct);
        await SyncAsync(cs, wiki, Sequenced(id, "作り直し", Ts.AddSeconds(2), 1));

        await using var verify = NewContext(cs);
        (await verify.Pages.AsNoTracking().SingleAsync(p => p.DocumentId == id, Ct)).Title.Should().Be("作り直し");
    }

    // Wiki.js の「パスごとの今の状態」（題名・非公開）を持つ偽物。`BeforeNextUpsert` は次の push の前に 1 度だけ走る。
    private sealed class StateWikiJs : IWikiJsClient, IWikiContentReader
    {
        public ConcurrentDictionary<string, (string Title, bool IsPrivate)> State { get; } = new();
        public Func<Task>? BeforeNextUpsert { get; set; }

        public async Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)
        {
            var before = BeforeNextUpsert;
            BeforeNextUpsert = null;
            if (before is not null) await before();
            State[page.Path] = (page.Title, page.IsPrivate);
        }

        public Task ArchivePageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeletePageAsync(string path, CancellationToken ct = default)
        {
            State.TryRemove(path, out _);
            return Task.CompletedTask;
        }

        public Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string> ReadAsync(string? markdownUri, string title, CancellationToken ct = default)
            => Task.FromResult($"# {title}");
    }
}
