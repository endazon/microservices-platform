using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.GraphDocuments.Sync;

// FR-17, UC-10, ADR-0033 決定 3・4・6, ADR-0035 決定 1, [[IADR-0521]] (#1396):
// **同じタグを持つ文書の組を辺で結ぶ**（利用者裁定 2026-10-09）。購読の受け口を経由して測る。
//
// 🔴 **1 通ごとに DbContext を作り直す**（本番の 1 メッセージ 1 スコープと同じ）。同じ文脈を使い回すと、
// 前の通の追跡が残り、「DB にはまだ無い追加」「DB にはまだ在る削除」の扱いを測れない。
// 🔴 否定形には陽性対照を対で置く（辺が 1 本も入らない壊れ方で緑にしない）。
[Trait("TestKind", "Unit")]
public class TagEdgeSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1");
    private static readonly Guid DocC = Guid.Parse("cccccccc-0000-0000-0000-0000000000c1");
    private static readonly Guid DocD = Guid.Parse("dddddddd-0000-0000-0000-0000000000d1");

    private sealed class Store
    {
        private readonly string _name = $"tags_{Guid.NewGuid():N}";
        private int _tick;

        public GraphDbContext Open() => new(
            new DbContextOptionsBuilder<GraphDbContext>().UseInMemoryDatabase(_name).Options);

        public int Reads { get; private set; }

        public async Task SeedTypesAsync(CancellationToken ct)
        {
            await using var db = Open();
            await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        }

        // 1 通の DocumentUpdated を新しい文脈で処理する。`body` が null なら本文は取れない。
        public async Task SendAsync(
            Guid id, string title, IEnumerable<string> tags, string? body = null, string? fingerprint = null,
            Dictionary<string, string>? attributes = null, int? max = null, ITagEdgeLocks? locks = null,
            CancellationToken ct = default)
        {
            await using var db = Open();
            var reader = new CountingReader(body, () => Reads++);
            var consumer = new GraphDocumentSyncConsumer(
                db, TimeProvider.System, reader, TagEdgesForTests.Links(db), new TermProfileSynchronizer(db),
                TagEdgesForTests.Synchronizer(db, max, locks), ConsumerTimeoutsForTests.Calls(), GraphSyncTimeouts.Default,
                NullLogger<GraphDocumentSyncConsumer>.Instance);
            _tick++;
            await consumer.Handle(new DocumentUpdated(
                id, title, "published", "storage://b/x.md",
                attributes ?? new Dictionary<string, string> { ["confidentiality"] = "internal" },
                tags.ToList(), T0.AddMinutes(_tick), fingerprint), ct);
        }

        public async Task DeleteAsync(
            Guid id, int? max = null, ITagEdgeLocks? locks = null, CancellationToken ct = default)
        {
            await using var db = Open();
            await new DocumentDeletedConsumer(db, TagEdgesForTests.Links(db), TagEdgesForTests.Synchronizer(db, max, locks),
                    NullLogger<DocumentDeletedConsumer>.Instance)
                .Handle(new DocumentDeleted(id, T0.AddDays(1)), ct);
        }

        public async Task<List<Edge>> EdgesAsync(CancellationToken ct)
        {
            await using var db = Open();
            return await db.Edges.AsNoTracking().ToListAsync(ct);
        }

        public async Task<HashSet<(Guid, Guid)>> TagPairsAsync(CancellationToken ct)
            => (await EdgesAsync(ct)).Where(e => e.IsTagDerived)
                .Select(e => (e.SourceDocumentId, e.TargetDocumentId)).ToHashSet();

        public async Task AddEdgeAsync(Edge edge, CancellationToken ct)
        {
            await using var db = Open();
            db.Edges.Add(edge);
            await db.SaveChangesAsync(ct);
        }

        public async Task<Guid> TypeIdAsync(string name, CancellationToken ct)
        {
            await using var db = Open();
            return (await db.EdgeTypes.FirstAsync(t => t.Name == name, ct)).Id;
        }
    }

    private sealed class CountingReader(string? body, Action onRead) : IGraphContentReader
    {
        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
        {
            onRead();
            return Task.FromResult(body);
        }
    }

    // [[IADR-0521]] 決定 7: 排他の要求を順に記録する。`whileWaitingForTags` は「タグの排他を待っている間に
    // 他の通が確定した」ことを再現する（InMemory は排他もトランザクションも持たないため、確定の時点を差し込む）。
    private sealed class RecordingLocks(Func<Task>? whileWaitingForTags = null) : ITagEdgeLocks
    {
        public List<string> Calls { get; } = [];

        public Task LockDocumentAsync(Guid documentId, CancellationToken ct)
        {
            Calls.Add($"doc:{documentId}");
            return Task.CompletedTask;
        }

        public async Task LockTagsAsync(IReadOnlyList<string> orderedTags, CancellationToken ct)
        {
            Calls.Add("tags:" + string.Join(",", orderedTags));
            if (whileWaitingForTags is not null)
                await whileWaitingForTags();
        }
    }

    private static (Guid, Guid) Pair(Guid x, Guid y) => x.CompareTo(y) < 0 ? (x, y) : (y, x);

    private static async Task<Store> NewStoreAsync(CancellationToken ct)
    {
        var store = new Store();
        await store.SeedTypesAsync(ct);
        return store;
    }

    // ── 受け入れ基準 1: 共有タグの辺ができ、タグを外すと消える ───────────────

    [Fact]
    public async Task 同じタグを持つ2文書の間にrelatedの共有タグの辺が1本できる_タグを外すと消える()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocA, "A", ["設計"], ct: ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty("相手がまだいない（1 文書のタグは辺を作らない）");

        await store.SendAsync(DocB, "B", ["設計"], ct: ct);

        var edges = await store.EdgesAsync(ct);
        var edge = edges.Should().ContainSingle().Subject;
        edge.EdgeTypeId.Should().Be(await store.TypeIdAsync("related", ct), "自動抽出の既定型（ADR-0033 決定 3）");
        edge.Provenance.Should().Be(EdgeProvenance.Auto, "出所の値は増やさない（ADR-0033 決定 4）");
        edge.AutoSource.Should().Be(EdgeAutoSource.Tag);
        edge.ExtractedFrom.Should().BeNull("共有タグの辺は起点を持たない（本文のリンクの差分に巻き込まれない）");
        (edge.SourceDocumentId, edge.TargetDocumentId).Should().Be(Pair(DocA, DocB), "対称型は (min, max)");

        // タグを外す（本文の指紋は変わらない —— タグだけの更新でも辺が追随すること）。
        await store.SendAsync(DocB, "B", [], ct: ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task 違うタグの文書は結ばない_陽性対照つき()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocA, "A", ["設計"], ct: ct);
        await store.SendAsync(DocB, "B", ["運用"], ct: ct);
        await store.SendAsync(DocC, "C", ["運用"], ct: ct);

        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo([Pair(DocB, DocC)],
            "B–C は結ばれ（陽性対照）、A はどちらとも結ばれない");
    }

    // ── 受け入れ基準 2: 組あたり 1 本・綴りの揺れ ─────────────────────────

    [Fact]
    public async Task タグを2つ共有しても辺は1本_大小文字と前後の空白だけが違うタグは同じタグ()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocA, "A", ["ops", "設計"], ct: ct);
        await store.SendAsync(DocB, "B", ["  OPS ", "設計"], ct: ct);

        (await store.EdgesAsync(ct)).Should().ContainSingle("組あたり 1 本（ux_edges と同じ粒度）");

        // 片方のタグを外しても、もう片方で結ばれ続ける。
        await store.SendAsync(DocB, "B", ["Ops"], ct: ct);
        (await store.EdgesAsync(ct)).Should().ContainSingle("大小文字だけが違う ops で結ばれ続ける");
    }

    [Fact]
    public async Task タグだけの更新では本文を読みに行かない()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocA, "A", ["設計"], body: "本文", fingerprint: "fp-a", ct: ct);
        var readsAfterFirst = store.Reads;
        readsAfterFirst.Should().Be(1, "陽性対照: 指紋が変わった最初の通は本文を読む");

        await store.SendAsync(DocB, "B", ["設計"], ct: ct);
        await store.SendAsync(DocA, "A", ["設計", "運用"], body: "本文", fingerprint: "fp-a", ct: ct);

        store.Reads.Should().Be(readsAfterFirst, "タグはイベントに載っている。ADR-0050 決定 3 の契機を増やさない");
        (await store.EdgesAsync(ct)).Should().ContainSingle();
    }

    // ── 受け入れ基準 3: ハブの上限 ─────────────────────────────────────

    [Fact]
    public async Task 上限ちょうどのタグは結ぶ_上限を超えたら所属文書の辺がすべて消え_下回ったら戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        const int max = 3;

        await store.SendAsync(DocA, "A", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocB, "B", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocC, "C", ["共通"], max: max, ct: ct);
        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo(
            [Pair(DocA, DocB), Pair(DocA, DocC), Pair(DocB, DocC)], "上限ちょうど（3 文書）は結ぶ");

        // 4 文書目で上限を超える。**D を含まない A–B・A–C・B–C も消える**（跨いだタグの所属を作り直す）。
        await store.SendAsync(DocD, "D", ["共通"], max: max, ct: ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty("ハブのタグからは辺を作らない");

        // D が抜けて上限以下へ戻る。D 以外の組が戻る。
        await store.SendAsync(DocD, "D", [], max: max, ct: ct);
        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo(
            [Pair(DocA, DocB), Pair(DocA, DocC), Pair(DocB, DocC)]);
    }

    [Fact]
    public async Task ハブのタグと別に共有するタグがあれば結ばれる()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        const int max = 2;

        await store.SendAsync(DocA, "A", ["ニュース", "トヨタ"], max: max, ct: ct);
        await store.SendAsync(DocB, "B", ["ニュース", "トヨタ"], max: max, ct: ct);
        await store.SendAsync(DocC, "C", ["ニュース"], max: max, ct: ct);

        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo([Pair(DocA, DocB)],
            "「ニュース」は 3 文書でハブ（上限 2）。A–B は「トヨタ」で結ばれる");
    }

    [Fact]
    public void 上限の不正値は既定へ倒す()
    {
        new TagEdgeOptions().EffectiveMaxDocumentsPerTag.Should().Be(GraphTraversal.MaxHubDegree,
            "既定は探索のハブ次数上限と同じ");
        new TagEdgeOptions { MaxDocumentsPerTag = 1 }.EffectiveMaxDocumentsPerTag.Should().Be(50);
        new TagEdgeOptions { MaxDocumentsPerTag = 1001 }.EffectiveMaxDocumentsPerTag.Should().Be(50);
        new TagEdgeOptions { MaxDocumentsPerTag = 2 }.EffectiveMaxDocumentsPerTag.Should().Be(2, "陽性対照");
        new TagEdgeOptions { MaxDocumentsPerTag = 1000 }.EffectiveMaxDocumentsPerTag.Should().Be(1000, "陽性対照");
    }

    // ── 受け入れ基準 4: 本文のリンク・利用者付与との関係 ───────────────────

    [Fact]
    public async Task 本文のリンクと共有タグが同じ組なら行は1本_リンクが消えたら同じ行が共有タグの辺へ戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        await store.SendAsync(DocB, "B", ["設計"], ct: ct);
        await store.SendAsync(DocA, "A", ["設計"], ct: ct);
        var tagEdge = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;
        tagEdge.AutoSource.Should().Be(EdgeAutoSource.Tag, "前提: 先に共有タグの辺がある");

        // A の本文が B を参照する。本文のリンクが同じ行を引き取る（重ねない）。
        await store.SendAsync(DocA, "A", ["設計"], body: "[[B]]", fingerprint: "fp-1", ct: ct);

        var linked = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;
        linked.Id.Should().Be(tagEdge.Id, "引き取る（消して入れ直さない）");
        linked.AutoSource.Should().Be(EdgeAutoSource.Link, "本文のリンクが同じ関係を表明している");
        linked.ExtractedFrom.Should().Be(DocA);

        // 本文からリンクを消す。組はタグを共有しているので、同じ行が共有タグの辺として残る。
        await store.SendAsync(DocA, "A", ["設計"], body: "リンクなし", fingerprint: "fp-2", ct: ct);

        var shared = (await store.EdgesAsync(ct)).Should().ContainSingle().Subject;
        shared.Id.Should().Be(linked.Id, "消して入れ直さない（同じ保存の削除と挿入は一意索引に当たり得る）");
        shared.AutoSource.Should().Be(EdgeAutoSource.Tag);
        shared.ExtractedFrom.Should().BeNull();

        // 陽性対照: タグも外せば消える。
        await store.SendAsync(DocA, "A", [], body: "リンクなし", fingerprint: "fp-2", ct: ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty();
    }

    // 本文のリンクの差分は、それ単体でも共有タグの辺に触れない（受け口の中では後段の共有タグの差分が
    // 戻してしまうため、差分の部品を直接呼んで追跡の状態で測る）。
    [Fact]
    public async Task 本文のリンクの差分は共有タグの辺を消さない()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        var related = await store.TypeIdAsync("related", ct);
        var tagEdge = Edge.Create(DocA, DocC, related, true, EdgeProvenance.Auto, autoSource: EdgeAutoSource.Tag);
        var linkEdge = Edge.Create(DocA, DocB, related, true, EdgeProvenance.Auto, extractedFrom: DocA);
        await store.AddEdgeAsync(tagEdge, ct);
        await store.AddEdgeAsync(linkEdge, ct);

        await using var db = store.Open();
        var result = await TagEdgesForTests.Links(db).SyncAsync(DocA, "リンクなし", ct);

        result.Removed.Should().Be(1, "陽性対照: 本文から消えたリンクの辺は消す");
        db.ChangeTracker.Entries<Edge>().Single(e => e.Entity.Id == tagEdge.Id).State
            .Should().Be(EntityState.Unchanged, "共有タグの辺は本文のリンクの差分の母集合に入らない");
    }

    [Fact]
    public async Task 利用者付与の同じ関係があれば共有タグの辺を重ねず_タグを外しても利用者の辺は消えない()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        var related = await store.TypeIdAsync("related", ct);
        var user = Edge.Create(DocA, DocB, related, true, EdgeProvenance.User);
        await store.AddEdgeAsync(user, ct);

        await store.SendAsync(DocA, "A", ["設計"], ct: ct);
        await store.SendAsync(DocB, "B", ["設計"], ct: ct);
        await store.SendAsync(DocC, "C", ["設計"], ct: ct);

        var edges = await store.EdgesAsync(ct);
        edges.Where(e => (e.SourceDocumentId, e.TargetDocumentId) == Pair(DocA, DocB))
            .Should().ContainSingle().Which.Provenance.Should().Be(EdgeProvenance.User, "人の辺を auto で覆わない");
        edges.Count(e => e.IsTagDerived).Should().Be(2, "陽性対照: A–C・B–C は共有タグの辺");

        await store.SendAsync(DocB, "B", [], ct: ct);
        (await store.EdgesAsync(ct)).Should().Contain(e => e.Id == user.Id, "ADR-0033 決定 6");
    }

    // ── 受け入れ基準 7: 撤収・削除 ──────────────────────────────────────

    [Fact]
    public async Task 露出OFFの撤収でタグと辺が消え_上限を下回ったタグの他の組が戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        const int max = 2;

        await store.SendAsync(DocA, "A", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocB, "B", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocC, "C", ["共通"], max: max, ct: ct);
        (await store.EdgesAsync(ct)).Should().BeEmpty("3 文書でハブ（前提）");

        await store.SendAsync(DocC, "C", ["共通"], attributes: new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            [DocumentExposure.GraphKey] = DocumentExposure.Excluded,
        }, max: max, ct: ct);

        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo([Pair(DocA, DocB)],
            "撤収した C はタグの件数から外れる。残ったタグの件数を数え直し、A–B が戻る");
        await using var db = store.Open();
        (await db.DocumentTags.AnyAsync(t => t.DocumentId == DocC, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task 削除でタグと辺が消え_上限を下回ったタグの他の組が戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        const int max = 2;

        await store.SendAsync(DocA, "A", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocB, "B", ["共通"], max: max, ct: ct);
        await store.SendAsync(DocC, "C", ["共通"], max: max, ct: ct);

        await store.DeleteAsync(DocC, max: max, ct: ct);

        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo([Pair(DocA, DocB)]);
        (await store.EdgesAsync(ct)).Should().NotContain(e => e.SourceDocumentId == DocC || e.TargetDocumentId == DocC);
        await using var db = store.Open();
        (await db.DocumentTags.AnyAsync(t => t.DocumentId == DocC, ct)).Should().BeFalse();
    }

    // ── 辞書が空（seed 前）: タグの複製は進め、辺は作らない ─────────────────

    [Fact]
    public async Task 既定型が辞書に無ければ共有タグの辺を作らない_タグの複製は進む()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new Store(); // seed しない

        await store.SendAsync(DocA, "A", ["設計"], ct: ct);
        await store.SendAsync(DocB, "B", ["設計"], ct: ct);

        (await store.EdgesAsync(ct)).Should().BeEmpty();
        await using var db = store.Open();
        (await db.DocumentTags.CountAsync(ct)).Should().Be(2, "陽性対照: タグの複製は保存される");
    }

    // ── 同時の受信（[[IADR-0521]] 決定 7）: タグ単位の排他 ───────────────────────

    // 🔴 排他は「文書 → 新旧のタグの和を序数順」で要求する。全通で順序が揃わないと互いに待ち合い、
    // 旧タグを取らないと、抜けたタグの件数（上限を跨いだか）を同時に入った文書と読み違える。
    [Fact]
    public async Task 排他は文書を先に取り_新旧のタグの和を序数順に要求する()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocA, "A", ["Zeta", "beta"], ct: ct);

        var locks = new RecordingLocks();
        await store.SendAsync(DocA, "A", ["alpha", "zeta", " Mid "], locks: locks, ct: ct);

        locks.Calls.Should().Equal($"doc:{DocA}", "tags:alpha,beta,mid,zeta");
    }

    [Fact]
    public async Task 削除も同じ排他を取る_旧タグを序数順に要求する()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);
        await store.SendAsync(DocA, "A", ["zeta", "alpha"], ct: ct);

        var locks = new RecordingLocks();
        await store.DeleteAsync(DocA, locks: locks, ct: ct);

        locks.Calls.Should().Equal($"doc:{DocA}", "tags:alpha,zeta");
    }

    // 🔴 所属・件数は排他を取った**後に**読む。待っている間に同じタグへ入った文書の確定を見落とすと、
    // 組の辺が欠けたまま残る（相手の通は、こちらの未確定の行を見られないので相手の側でも張らない）。
    [Fact]
    public async Task 排他を待つ間に同じタグへ入った文書の確定を読み_組の辺を張る()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await NewStoreAsync(ct);

        // B の通は A の未確定の行を見られない（A の通の保存前に確定する）。
        var locks = new RecordingLocks(() => store.SendAsync(DocB, "B", ["設計"], ct: ct));
        await store.SendAsync(DocA, "A", ["設計"], locks: locks, ct: ct);

        locks.Calls.Should().HaveCount(2, "陽性対照: 排他を要求し、待つ間に B が確定した");
        (await store.TagPairsAsync(ct)).Should().BeEquivalentTo([Pair(DocA, DocB)]);
    }
}
