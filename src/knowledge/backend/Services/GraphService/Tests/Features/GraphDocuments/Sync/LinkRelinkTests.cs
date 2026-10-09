using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.GraphDocuments.Sync;

// FR-17, UC-10, ADR-0033 決定 5・6・8, [[IADR-0281]], [[IADR-0389]], [[IADR-0522]] (#1396):
// **Wiki のリンク（`doc/<ID>`）の解決**と、**後から届いた相手へのリンク**（後着の作り直し）。
//
// 🔴 1 通ごとに DbContext を作り直す（`TagEdgeSyncTests` と同じ理由）。
[Trait("TestKind", "Unit")]
public class LinkRelinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1");
    private static readonly Guid DocC = Guid.Parse("cccccccc-0000-0000-0000-0000000000c1");

    private sealed class Store
    {
        private readonly string _name = $"relink_{Guid.NewGuid():N}";
        private int _tick;

        public GraphDbContext Open() => new(
            new DbContextOptionsBuilder<GraphDbContext>().UseInMemoryDatabase(_name).Options);

        public async Task SeedTypesAsync(CancellationToken ct)
        {
            await using var db = Open();
            await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        }

        // 本文つきの通（本文が変わったことにするため、通ごとに指紋を変える）。
        public async Task SendAsync(Guid id, string title, string? body, CancellationToken ct)
        {
            await using var db = Open();
            var consumer = new GraphDocumentSyncConsumer(
                db, TimeProvider.System, new FixedReader(body), TagEdgesForTests.Links(db),
                new TermProfileSynchronizer(db), TagEdgesForTests.Synchronizer(db),
                ConsumerTimeoutsForTests.Calls(), GraphSyncTimeouts.Default,
                NullLogger<GraphDocumentSyncConsumer>.Instance);
            _tick++;
            await consumer.Handle(new DocumentUpdated(
                id, title, "published", "storage://b/x.md",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                [], T0.AddMinutes(_tick), body is null ? null : $"fp-{_tick}"), ct);
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            await using var db = Open();
            await new DocumentDeletedConsumer(db, TagEdgesForTests.Links(db), TagEdgesForTests.Synchronizer(db),
                    NullLogger<DocumentDeletedConsumer>.Instance)
                .Handle(new DocumentDeleted(id, T0.AddDays(1)), ct);
        }

        public async Task<List<Edge>> EdgesAsync(CancellationToken ct)
        {
            await using var db = Open();
            return await db.Edges.AsNoTracking().ToListAsync(ct);
        }

        public async Task<Guid> TypeIdAsync(string name, CancellationToken ct)
        {
            await using var db = Open();
            return (await db.EdgeTypes.FirstAsync(t => t.Name == name, ct)).Id;
        }
    }

    private sealed class FixedReader(string? body) : IGraphContentReader
    {
        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
            => Task.FromResult(body);
    }

    private static async Task<Store> NewStoreAsync(CancellationToken ct)
    {
        var store = new Store();
        await store.SeedTypesAsync(ct);
        return store;
    }

    // ── 受け入れ基準 5: Wiki のリンク ─────────────────────────────────────

    [Theory]
    [InlineData("[設計](/doc/{0})")]
    [InlineData("[設計](/en/doc/{0})")]
    [InlineData("[設計](https://wiki.example/ja/doc/{0}?x=1#見出し)")]
    [InlineData("[設計](doc/{0})")]
    public async Task Wikiのリンクは文書IDで解決する(string template)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocB, "設計書", body: null, ct);

        await store.SendAsync(DocA, "A", string.Format(template, DocB), ct);

        var edge = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;
        new[] { edge.SourceDocumentId, edge.TargetDocumentId }.Should().BeEquivalentTo([DocA, DocB]);
        edge.EdgeTypeId.Should().Be(await store.TypeIdAsync("related", ct), "標準 Markdown リンクの既定型");
        edge.AutoSource.Should().Be(EdgeAutoSource.Link);
    }

    [Fact]
    public async Task 存在しない文書IDのWikiのリンクは辺を作らず未解決に数える()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocB, "設計書", body: null, ct);
        var missing = Guid.Parse("99999999-0000-0000-0000-000000000009");

        await store.SendAsync(DocA, "A", $"[x](/doc/{missing}) と [y](/doc/{DocB})", ct);

        (await store.EdgesAsync(ct)).Should().ContainSingle("陽性対照: 存在する B へは張る");
        await using var db = store.Open();
        var collector = new KnowledgeHealthCollector(db, null!, null!, null!,
            NullLogger<KnowledgeHealthCollector>.Instance);
        var unresolved = await collector.CollectUnresolvedLinksAsync(ct);
        unresolved.Should().ContainSingle("存在しない ID の 1 本だけが未解決").Which.Dimension
            .Should().Be(LinkTargetMatcher.NotFoundDimension);
    }

    [Fact]
    public async Task 同じ名前を構文違いで複数回指しても未解決は1件に数える()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocA, "A", "[[未着]] と [[未着#見出し]] と ![[未着]]", ct);

        await using var db = store.Open();
        (await db.DocumentLinkTargets.CountAsync(ct)).Should().Be(3, "前提: 1 リンク 1 行で保存される");
        var collector = new KnowledgeHealthCollector(db, null!, null!, null!,
            NullLogger<KnowledgeHealthCollector>.Instance);
        (await collector.CollectUnresolvedLinksAsync(ct)).Should().ContainSingle("数える単位は従前どおり (起点, 名前)");
    }

    // ── 受け入れ基準 6: 後着の相手 ────────────────────────────────────────

    [Fact]
    public async Task 先に届いた文書のリンクは_後から届いた相手へ辺を張る_構文の別とアンカーを保つ()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        // A が先に届く。B・C はまだ無い（未解決）。
        await store.SendAsync(DocA, "A", $"[[B#手順]] と ![[C]] と [w](/doc/{DocC})", ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty("前提: 相手が未着");

        // B・C が後から届く（本文は無い —— A の本文は読み直さない）。
        await store.SendAsync(DocB, "B", body: null, ct);
        await store.SendAsync(DocC, "C", body: null, ct);

        var edges = await store.EdgesAsync(ct);
        var cites = await store.TypeIdAsync("cites", ct);
        var embeds = await store.TypeIdAsync("embeds", ct);
        var related = await store.TypeIdAsync("related", ct);
        edges.Should().HaveCount(3);
        edges.Should().Contain(e => e.SourceDocumentId == DocA && e.TargetDocumentId == DocB
            && e.EdgeTypeId == cites && e.TargetAnchor == "手順", "見出し付きは cites・アンカーを保つ（ADR-0033 決定 5・8）");
        edges.Should().Contain(e => e.SourceDocumentId == DocA && e.TargetDocumentId == DocC
            && e.EdgeTypeId == embeds, "埋め込みは embeds");
        edges.Should().Contain(e => e.EdgeTypeId == related, "Wiki のリンクも後着で張る");
        edges.Should().OnlyContain(e => e.ExtractedFrom == DocA && e.AutoSource == EdgeAutoSource.Link);
    }

    [Fact]
    public async Task 改名で外れたリンクの辺は消え_新しい名前を指していたリンクの辺ができる()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocB, "旧名", body: null, ct);
        await store.SendAsync(DocA, "A", "[[旧名]]", ct);
        await store.SendAsync(DocC, "C", "[[新名]]", ct);
        (await store.EdgesAsync(ct)).Should().ContainSingle(e => e.ExtractedFrom == DocA, "前提");

        await store.SendAsync(DocB, "新名", body: null, ct);

        var edges = await store.EdgesAsync(ct);
        edges.Should().ContainSingle();
        edges[0].ExtractedFrom.Should().Be(DocC, "A の [[旧名]] は外れ、C の [[新名]] が張られる");
    }

    [Fact]
    public async Task 同名の文書が届くと曖昧になって辺が消え_片方を削除すると戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocB, "同名", body: null, ct);
        await store.SendAsync(DocA, "A", "[[同名]]", ct);
        (await store.EdgesAsync(ct)).Should().ContainSingle("前提: 一意なので解決する");

        await store.SendAsync(DocC, "同名", body: null, ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty("同名 2 件は曖昧（IADR-0389）");

        await store.DeleteAsync(DocC, ct);
        var edge = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;
        new[] { edge.SourceDocumentId, edge.TargetDocumentId }.Should().BeEquivalentTo([DocA, DocB]);
    }

    [Fact]
    public async Task 構文の別を持たない移行前の行しか無い起点は作り直さない()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        // 移行前の形（名前だけ）を直接入れる。
        await using (var db = store.Open())
        {
            db.Documents.Add(GraphDocument.Create(DocA, "A",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-a", T0));
            db.DocumentLinkTargets.Add(DocumentLinkTarget.Create(DocA, "B", T0));
            await db.SaveChangesAsync(ct);
        }

        await store.SendAsync(DocB, "B", body: null, ct);

        (await store.EdgesAsync(ct)).Should().BeEmpty(
            "型が分からない行から作ると [[B#h]]（cites）や ![[B]]（embeds）を related へ落とす");

        // 移行前に抽出済みの辺（A → B・cites）は、B の改名で起点 A が作り直しの対象に入っても**消さない**
        // （作り直すと、行の無いリンクを「本文から消えた」と読んで消す）。
        var cites = await store.TypeIdAsync("cites", ct);
        await using (var db = store.Open())
        {
            db.Edges.Add(Edge.Create(DocA, DocB, cites, false, EdgeProvenance.Auto, targetAnchor: "h", extractedFrom: DocA));
            await db.SaveChangesAsync(ct);
        }
        await store.SendAsync(DocB, "B 改", body: null, ct);
        (await store.EdgesAsync(ct)).Should().ContainSingle(e => e.EdgeTypeId == cites, "移行前の起点は作り直さない");
        await store.SendAsync(DocB, "B", body: null, ct);

        // 陽性対照: 本文が変わって行が作り直されれば、後着の相手にも張れる。
        await store.SendAsync(DocA, "A", "[[C]]", ct);
        await store.SendAsync(DocC, "C", body: null, ct);
        (await store.EdgesAsync(ct)).Should().ContainSingle(e => e.ExtractedFrom == DocA);
    }

    [Fact]
    public async Task 本文が変わらない通では作り直さない_題名が同じなら起点を引かない()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocB, "B", body: null, ct);
        await store.SendAsync(DocA, "A", "[[B]]", ct);
        var before = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;

        // B の同じ題名の再送（再発行と同じ形）。行は入れ替わらない。
        await store.SendAsync(DocB, "B", body: null, ct);

        (await store.EdgesAsync(ct)).Should().ContainSingle().Which.Id.Should().Be(before.Id);
    }
}
