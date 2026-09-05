using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.GraphDocuments.Sync;

// FR-10, FR-17, UC-05, UC-10, SC-10, ADR-0033 決定 4・6, [[IADR-0389]] 決定 3 (#1246):
// **本文が指すリンク先の名前**の保存。未解決リンク数（`unresolved-links`）の材料である。
//
// 🔴 **保存するのは解決の失敗ではなく名前である。** 判定は収集のたびにやり直す
// （相手の改名・削除で壊れたリンクを拾うため。証拠は `KnowledgeHealthNewIndicatorTests`）。
// ここで固定するのは**材料が正しく置かれ、正しく消えるか**である:
//
//   1. 解決できたものも、できなかったものも保存する
//   2. 再取り込みで**全量置換**する（本文から消えたリンクの行が残らない）
//   3. リンクが 0 本になっても置換する（**短絡して行を残さない**）
//   4. 文書の削除でその文書が**書いた**行は消え、その文書を**指す**行は残る
[Trait("TestKind", "Unit")]
public class DocumentLinkTargetPersistenceTests
{
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-1246-0000-0000-0000000000a1");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-1246-0000-0000-0000000000b1");
    private static readonly Guid DocOther = Guid.Parse("dddddddd-1246-0000-0000-0000000000d1");
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"linktargets_{Guid.NewGuid():N}").Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubReader(string? body) : IGraphContentReader
    {
        public string? Body { get; set; } = body;

        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
            => Task.FromResult(Body);
    }

    private static async Task<GraphDbContext> SeedAsync(CancellationToken ct)
    {
        var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        db.Documents.Add(GraphDocument.Create(
            DocB, "文書B", new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-b", T0));
        await db.SaveChangesAsync(ct);
        return db;
    }

    private static (GraphDocumentSyncConsumer Consumer, StubReader Reader) Build(
        GraphDbContext db, string? body)
    {
        var reader = new StubReader(body);
        var sync = new LinkEdgeSynchronizer(
            db, new EdgeTypeFallbackMetrics(NewMeterFactory()),
            NullLogger<LinkEdgeSynchronizer>.Instance);
        var consumer = new GraphDocumentSyncConsumer(
            db, new FixedClock(T0.AddDays(1)), reader, sync,
            new TermProfileSynchronizer(db),
            NullLogger<GraphDocumentSyncConsumer>.Instance);
        return (consumer, reader);
    }

    // 指紋を変えないと本文を読み直さない（差分更新の既定）。周回ごとに変える。
    private static DocumentUpdated Event(string fingerprint)
        => new(DocA, "文書A", "published", "storage://b/a.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], T0, fingerprint);

    // 計器は本クラスの主張ではない（`EdgeTypeFallbackMetrics` は `LinkEdgeSyncTests` が測る）。
    // 器だけを渡す。
    private static System.Diagnostics.Metrics.IMeterFactory NewMeterFactory()
        => new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddMetrics().BuildServiceProvider()
            .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();

    private static Task<List<string>> TargetsOfAsync(GraphDbContext db, Guid source, CancellationToken ct)
        => db.DocumentLinkTargets.Where(t => t.SourceDocumentId == source)
            .Select(t => t.Target).OrderBy(t => t).ToListAsync(ct);

    // ── 1. 解決の可否によらず保存する ─────────────────────────────

    // 🔴 **解決できたものも保存する。** 「いま解決できた」を根拠に捨てると、
    // 相手が後から改名・削除されたときの壊れ方を永久に取りこぼす（決定 3）。
    [Fact]
    public async Task 解決できたリンクもできなかったリンクも保存される()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _) = Build(db, "実在する [[文書B]] と、実在しない [[幻の文書]]。");

        await consumer.Handle(Event("fp-1"), ct);

        (await TargetsOfAsync(db, DocA, ct)).Should().BeEquivalentTo(["幻の文書", "文書B"]);

        // 陽性対照: 解決できた側は**辺にもなっている**（保存が「辺を作らない」の副作用ではない）。
        (await db.Edges.CountAsync(ct)).Should().Be(1, "実在する相手にだけ辺が張られる");
    }

    // ── 2. 全量置換 ──────────────────────────────────────────────

    [Fact]
    public async Task 再取り込みでリンク先は全量置換される()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, reader) = Build(db, "[[古い相手]]");
        await consumer.Handle(Event("fp-1"), ct);
        // 陽性対照: 1 周目で確かに入っている。
        (await TargetsOfAsync(db, DocA, ct)).Should().BeEquivalentTo(["古い相手"]);

        reader.Body = "[[新しい相手]]";
        await consumer.Handle(Event("fp-2"), ct);

        (await TargetsOfAsync(db, DocA, ct)).Should().BeEquivalentTo(["新しい相手"],
            "本文から消えたリンクの行が残ると、未解決リンク数が恒久的に減らない");
    }

    // 🔴 **境界。リンクが 0 本になっても置換する。**
    // 「リンクが無ければ何もしない」と短絡すると、リンクを全部消した文書の行が残り続ける。
    [Fact]
    public async Task 本文からリンクを全部消すとリンク先の行も消える()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, reader) = Build(db, "[[幻の文書]]");
        await consumer.Handle(Event("fp-1"), ct);
        (await TargetsOfAsync(db, DocA, ct)).Should().ContainSingle("陽性対照: 1 周目で入っている");

        reader.Body = "リンクの無い本文になった。";
        await consumer.Handle(Event("fp-2"), ct);

        (await TargetsOfAsync(db, DocA, ct)).Should().BeEmpty();
    }

    // 同じ相手を本文で 2 度指しても行は 1 本（未解決リンク数を書き方で水増ししない）。
    [Fact]
    public async Task 同じ相手を複数回指しても行は1本である()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _) = Build(db, "[[幻の文書]] と、もう一度 [[幻の文書]]。");

        await consumer.Handle(Event("fp-1"), ct);

        (await TargetsOfAsync(db, DocA, ct)).Should().BeEquivalentTo(["幻の文書"]);
    }

    // ── 3. 文書の削除 ────────────────────────────────────────────

    // 🔴 **向きで扱いが違う。対で測る。**
    //   消す  … 消えた文書が**書いた**リンク（指す先が無くなったのではなく、書き手が消えた）
    //   残す  … 消えた文書を**指す**リンク（あちらの本文はいま壊れた。未解決に数えるのが正しい）
    [Fact]
    public async Task 文書を削除すると書いた側の行だけが消え指す側の行は残る()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        db.Documents.Add(GraphDocument.Create(DocOther, "他の文書", [], "fp-o", T0));
        db.DocumentLinkTargets.AddRange(
            DocumentLinkTarget.Create(DocA, "文書B", T0),        // 消える文書が書いた
            DocumentLinkTarget.Create(DocOther, "文書A", T0));   // 消える文書を指している
        db.Documents.Add(GraphDocument.Create(DocA, "文書A", [], "fp-a", T0));
        await db.SaveChangesAsync(ct);

        var consumer = new DocumentDeletedConsumer(db, NullLogger<DocumentDeletedConsumer>.Instance);
        await consumer.Handle(new DocumentDeleted(DocA, T0.AddDays(1)), ct);

        (await TargetsOfAsync(db, DocA, ct)).Should().BeEmpty(
            "消えた文書が書いたリンクを残すと、未解決リンク数に永久に積み上がる");
        (await TargetsOfAsync(db, DocOther, ct)).Should().BeEquivalentTo(["文書A"],
            "他文書のリンクはいま壊れたのであり、未解決として数えられるのが正しい");
    }
}
