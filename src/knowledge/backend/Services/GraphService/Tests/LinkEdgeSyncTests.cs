using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Features.GraphDocuments;
using GraphService.Domain;
using GraphService.Common.Observability;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests;

// FR-17, UC-10, ADR-0033 決定 3・4・6・8, ADR-0050 決定 3, IADR-0281 (#912):
// **リンク抽出と辺の差分更新**（consumer 経由の統合）。
//
// #912 の受け入れ基準 4 件をここで測る。
//   1. 3 層それぞれの記法が期待した既定型へ写像される
//   2. 未定義型が related へフォールバックし、警告が 1 件記録される
//   3. 差分更新で利用者付与・AI 承認済みの辺が保存される（自動抽出のみ置換）
//   4. 解決できないリンクで辺が作られない
//
// 🔴 **否定形テストには必ず陽性対照を対で置く**（GraphTraversalTests と同じ作法）——
// 「消えていない」だけでは、そもそも辺が入っていなくても緑になる。
public class LinkEdgeSyncTests
{
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid DocB = Guid.Parse("11111111-0000-0000-0000-0000000000b1");
    private static readonly Guid DocC = Guid.Parse("cccccccc-0000-0000-0000-0000000000c1");
    private static readonly Guid DocOther = Guid.Parse("dddddddd-0000-0000-0000-0000000000d1");
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"link_{Guid.NewGuid():N}").Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // 本文取得のスタブ。**呼ばれた回数を数える**（指紋が変わらないときに読まないことの観測点）。
    private sealed class StubReader(string? body) : IGraphContentReader
    {
        public string? Body { get; set; } = body;
        public int Calls { get; private set; }

        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Body);
        }
    }

    // 辞書（seed 相当）を入れる。**辺の型は実行時辞書であり、コード定義ではない。**
    private static async Task<GraphDbContext> SeedAsync(CancellationToken ct, params string[] titles)
    {
        var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        db.Documents.AddRange(
            GraphDocument.Create(DocB, titles.Length > 0 ? titles[0] : "文書B",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-b", T0),
            GraphDocument.Create(DocC, titles.Length > 1 ? titles[1] : "文書C",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-c", T0));
        await db.SaveChangesAsync(ct);
        return db;
    }

    private static (GraphDocumentSyncConsumer Consumer, StubReader Reader, MetricsProbe Probe)
        Build(GraphDbContext db, string? body)
    {
        var factory = new TestMeterFactory();
        var metrics = new EdgeTypeFallbackMetrics(factory);
        var probe = new MetricsProbe(factory.CreatedMeterName!);
        var reader = new StubReader(body);
        var sync = new LinkEdgeSynchronizer(db, metrics, NullLogger<LinkEdgeSynchronizer>.Instance);
        var consumer = new GraphDocumentSyncConsumer(
            db, new FixedClock(T0.AddDays(1)), reader, sync,
            NullLogger<GraphDocumentSyncConsumer>.Instance);
        return (consumer, reader, probe);
    }

    private static DocumentUpdated Event(
        Guid? docId = null, string? fingerprint = "fp-1", DateTimeOffset? updatedAt = null)
        => new(docId ?? DocA, "文書A", "published", "storage://b/a.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" },
            ["ops"], updatedAt ?? T0, fingerprint);

    private static async Task<Guid> TypeIdAsync(GraphDbContext db, string name, CancellationToken ct)
        => (await db.EdgeTypes.FirstAsync(t => t.Name == name, ct)).Id;

    // ── 受け入れ基準 1: 3 層の記法が期待した既定型へ写像される ─────────────────

    [Fact]
    public async Task 三層の記法がそれぞれの既定型の辺になる_出所と抽出起点が入る()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        const string body = """
            ---
            supersedes: [[文書C]]
            ---

            一般参照 [[文書B]]、埋め込み ![[文書C]]。
            """;
        var (consumer, _, _) = Build(db, body);

        await consumer.Handle(Event(), ct);

        var edges = await db.Edges.ToListAsync(ct);
        edges.Should().HaveCount(3);
        edges.Should().AllSatisfy(e =>
        {
            e.Provenance.Should().Be(EdgeProvenance.Auto, "本文からの自動抽出である（ADR-0033 決定 4）");
            e.ExtractedFrom.Should().Be(DocA, "差分の母集合を当該文書起点に絞るための列（IADR-0281）");
        });

        var types = await db.EdgeTypes.ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        edges.Select(e => types[e.EdgeTypeId]).Should().BeEquivalentTo(
            ["supersedes", "related", "embeds"],
            "① 明示指定 → supersedes ／ ③ 一般参照 → related ／ ② 埋め込み → embeds");
    }

    [Fact]
    public async Task 見出し指定つき参照はcitesの辺になりアンカーが記録される()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _, _) = Build(db, "根拠は [[文書B#第2章]] にある。");

        await consumer.Handle(Event(), ct);

        var edge = await db.Edges.SingleAsync(ct);
        edge.EdgeTypeId.Should().Be(await TypeIdAsync(db, "cites", ct));
        edge.TargetAnchor.Should().Be("第2章", "ADR-0033 決定 5: 見出し指定を to_anchor へ記録する");
        edge.SourceAnchor.Should().BeEmpty("アンカー未指定は空文字（NULL にしない）");
    }

    [Fact]
    public async Task 標準Markdownリンクもrelatedの辺になる()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _, _) = Build(db, "詳細は [文書B](docs/文書B.md) を見よ。");

        await consumer.Handle(Event(), ct);

        var edge = await db.Edges.SingleAsync(ct);
        edge.EdgeTypeId.Should().Be(await TypeIdAsync(db, "related", ct));
    }

    // ── 受け入れ基準 2: 未定義型は related へ丸め、警告を 1 件記録する ─────────

    [Fact]
    public async Task 未定義型はrelatedへ丸められカウンタが1件記録される()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        const string body = """
            ---
            contradicts: [[文書B]]
            ---

            本文。
            """;
        var (consumer, _, probe) = Build(db, body);

        await consumer.Handle(Event(), ct);

        var edge = await db.Edges.SingleAsync(ct);
        edge.EdgeTypeId.Should().Be(await TypeIdAsync(db, "related", ct),
            "拒否すると取り込み全体が落ち、破棄すると辺そのものが失われる（ADR-0033 決定 3）");
        probe.Total.Should().Be(1, "フォールバックの発生件数は観測できなければならない（決定 3）");
        probe.Layers.Should().Equal([EdgeTypeFallbackMetrics.ExplicitLayer]);
    }

    [Fact]
    public async Task 定義済みの型ではカウンタが動かない_陽性対照()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        const string body = """
            ---
            supersedes: [[文書B]]
            ---

            本文。
            """;
        var (consumer, _, probe) = Build(db, body);

        await consumer.Handle(Event(), ct);

        (await db.Edges.SingleAsync(ct)).EdgeTypeId
            .Should().Be(await TypeIdAsync(db, "supersedes", ct));
        probe.Total.Should().Be(0, "0 が正常である（同じ経路で 1 件が出る上のテストが陽性対照）");
    }

    // ── 受け入れ基準 3: 自動抽出のみ置換する（差分更新。ADR-0033 決定 6） ──────

    [Fact]
    public async Task 消えたリンクの自動抽出辺だけが消える_利用者付与とAI承認済みと他文書起点は残る()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var relatedId = await TypeIdAsync(db, "related", ct);
        var citesId = await TypeIdAsync(db, "cites", ct);

        // 初回: [[文書B]] と [[文書C]] の 2 本を自動抽出する。
        var (consumer, reader, _) = Build(db, "[[文書B]] と [[文書C]]。");
        await consumer.Handle(Event(fingerprint: "fp-1"), ct);
        db.Edges.Count().Should().Be(2, "陽性対照: 初回で自動抽出の辺が 2 本入っている");

        // 触ってはならない 3 種を足す。
        db.Edges.AddRange(
            Edge.Create(DocA, DocC, citesId, isSymmetric: false, EdgeProvenance.User),
            Edge.Create(DocA, DocB, citesId, isSymmetric: false, EdgeProvenance.AiApproved),
            // 他文書（DocOther）の本文から抽出された auto 辺。端点に DocA を含むが起点は別文書である。
            Edge.Create(DocOther, DocA, citesId, isSymmetric: false, EdgeProvenance.Auto,
                extractedFrom: DocOther));
        await db.SaveChangesAsync(ct);

        // 再取り込み: [[文書C]] が本文から消えた。
        reader.Body = "[[文書B]] だけになった。";
        await consumer.Handle(Event(fingerprint: "fp-2", updatedAt: T0.AddMinutes(1)), ct);

        var edges = await db.Edges.ToListAsync(ct);
        edges.Should().HaveCount(4, "消えるのは DocA 起点の auto 辺のうち本文から消えた 1 本だけ");
        edges.Where(e => e.Provenance == EdgeProvenance.User).Should().ContainSingle(
            "利用者付与の辺は再取り込みで消さない（ADR-0033 決定 6）");
        edges.Where(e => e.Provenance == EdgeProvenance.AiApproved).Should().ContainSingle(
            "承認済み AI 提案の辺は再取り込みで消さない（ADR-0033 決定 6）");
        edges.Where(e => e.ExtractedFrom == DocOther).Should().ContainSingle(
            "他文書起点の auto 辺は向こうの本文が正本であり、こちらの差分では消さない");
        edges.Where(e => e.ExtractedFrom == DocA).Should().ContainSingle(
            "残るのは本文になお在る [[文書B]] の 1 本");
        edges.Single(e => e.ExtractedFrom == DocA).EdgeTypeId.Should().Be(relatedId);
    }

    [Fact]
    public async Task 利用者が既に張った同じ関係へ自動抽出の辺を重ねない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var relatedId = await TypeIdAsync(db, "related", ct);
        db.Edges.Add(Edge.Create(DocA, DocB, relatedId, isSymmetric: true, EdgeProvenance.User));
        await db.SaveChangesAsync(ct);

        var (consumer, _, _) = Build(db, "[[文書B]] を参照。");
        await consumer.Handle(Event(), ct);

        var edge = await db.Edges.SingleAsync(ct);
        edge.Provenance.Should().Be(EdgeProvenance.User,
            "ux_edges は同一の 5 つ組を 1 行しか許さない。人の辺を auto で覆わない");
    }

    // ── 冪等性（at-least-once・再配信） ─────────────────────────────────────

    [Fact]
    public async Task 同じ本文を再取り込みしても辺の行は入れ替わらない_冪等()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _, _) = Build(db, "[[文書B]] と [[文書C]]。");
        await consumer.Handle(Event(fingerprint: "fp-1"), ct);
        var before = (await db.Edges.AsNoTracking().ToListAsync(ct)).Select(e => e.Id).ToList();

        // 指紋だけ変わり、本文は同じ（例: 空白の変更）。
        await consumer.Handle(Event(fingerprint: "fp-2", updatedAt: T0.AddMinutes(1)), ct);

        var after = (await db.Edges.AsNoTracking().ToListAsync(ct)).Select(e => e.Id).ToList();
        after.Should().BeEquivalentTo(before, "差分ゼロなら行は削除も再作成もされない");
    }

    [Fact]
    public async Task 指紋が変わらないイベントでは本文を読みに行かない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, reader, _) = Build(db, "[[文書B]]。");
        await consumer.Handle(Event(fingerprint: "fp-1"), ct);
        reader.Calls.Should().Be(1, "陽性対照: 指紋が変わった初回は読みに行く");

        // 属性・タグだけの更新（指紋は同じ）。ADR-0050 決定 3。
        await consumer.Handle(Event(fingerprint: "fp-1", updatedAt: T0.AddMinutes(1)), ct);

        reader.Calls.Should().Be(1, "本文が変わっていないなら storage を叩かない（ADR-0050 決定 3）");
        db.Edges.Count().Should().Be(1);
    }

    [Fact]
    public async Task 指紋がnullのイベントでは抽出しない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, reader, _) = Build(db, "[[文書B]]。");

        await consumer.Handle(Event(fingerprint: null), ct);

        reader.Calls.Should().Be(0, "指紋 null は「不明」であり本文が変わった証拠にならない");
        db.Edges.Should().BeEmpty();
    }

    // ── 受け入れ基準 4: 解決できないリンクで辺を作らない ────────────────────

    [Fact]
    public async Task 不在_曖昧_自己参照のリンクでは辺が作られない_陽性対照つき()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        // 同名の 2 文書（曖昧）と、当該文書自身のノード。
        db.Documents.AddRange(
            GraphDocument.Create(DocOther, "文書C",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-o", T0),
            GraphDocument.Create(DocA, "文書A",
                new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-0", T0));
        await db.SaveChangesAsync(ct);

        var (consumer, _, _) = Build(db,
            "[[存在しない文書]] と [[文書C]]（曖昧）と [[文書A]]（自己）と [[文書B]]（陽性対照）。");
        await consumer.Handle(Event(fingerprint: "fp-9", updatedAt: T0.AddMinutes(1)), ct);

        var edges = await db.Edges.ToListAsync(ct);
        edges.Should().ContainSingle("解決できたのは陽性対照の [[文書B]] だけである");
        edges[0].OtherEnd(DocA).Should().Be(DocB);
    }

    [Fact]
    public async Task タイトルは大文字小文字を無視して一意なら解決する()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct, "Design Note", "文書C");
        var (consumer, _, _) = Build(db, "[[design note]] を参照。");

        await consumer.Handle(Event(), ct);

        (await db.Edges.SingleAsync(ct)).OtherEnd(DocA).Should().Be(DocB);
    }

    // ── 縮退（本文が取れない）: 辺を一切触らない ────────────────────────────

    [Fact]
    public async Task 本文が取得できないときは既存の自動抽出辺が消えない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, reader, _) = Build(db, "[[文書B]] と [[文書C]]。");
        await consumer.Handle(Event(fingerprint: "fp-1"), ct);
        db.Edges.Count().Should().Be(2, "陽性対照: 縮退の前に辺が 2 本入っている");

        // ストレージ未配備・URI 未指定などで本文が取れない。
        reader.Body = null;
        await consumer.Handle(Event(fingerprint: "fp-2", updatedAt: T0.AddMinutes(1)), ct);

        db.Edges.Count().Should().Be(2,
            "縮退本文で抽出すると『全リンクが消えた』と解釈され、辺が全消しになる（IADR-0281）");
    }

    // ── 対称型の正規化（IADR-0242 決定 9） ──────────────────────────────────

    [Fact]
    public async Task 対称型は正規化され再処理で差分が出ない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        var (consumer, _, _) = Build(db, "[[文書B]] を参照。");
        await consumer.Handle(Event(fingerprint: "fp-1"), ct);

        var edge = await db.Edges.AsNoTracking().SingleAsync(ct);
        // DocA は DocB より大きい GUID なので、対称型は (DocB, DocA) へ正規化される。
        DocA.CompareTo(DocB).Should().BeGreaterThan(0, "この前提が崩れるとテストが何も測らない");
        edge.SourceDocumentId.Should().Be(DocB);
        edge.TargetDocumentId.Should().Be(DocA);
        edge.ExtractedFrom.Should().Be(DocA, "正規化で端点は入れ替わるが、抽出の起点は入れ替えない");

        // 再処理: 正規化後のキーで突き合わせるので差分は出ない。
        await consumer.Handle(Event(fingerprint: "fp-2", updatedAt: T0.AddMinutes(1)), ct);

        var after = await db.Edges.AsNoTracking().SingleAsync(ct);
        after.Id.Should().Be(edge.Id, "差分ゼロ。削除→再作成が起きると Id が変わる");
    }

    [Fact]
    public async Task 相互リンクは1行に落ちる_起点は先に抽出した側になる()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = await SeedAsync(ct);
        db.Documents.Add(GraphDocument.Create(DocA, "文書A",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-0", T0));
        await db.SaveChangesAsync(ct);

        // DocA → 文書B
        var (consumerA, _, _) = Build(db, "[[文書B]]");
        await consumerA.Handle(Event(docId: DocA, fingerprint: "fp-a2", updatedAt: T0.AddMinutes(1)), ct);
        // DocB → 文書A（逆向きの同じ関係）
        var (consumerB, _, _) = Build(db, "[[文書A]]");
        await consumerB.Handle(Event(docId: DocB, fingerprint: "fp-b2", updatedAt: T0.AddMinutes(2)), ct);

        var edges = await db.Edges.ToListAsync(ct);
        edges.Should().ContainSingle("対称型は (min, max) へ正規化され 1 行になる（IADR-0242 決定 9）");
        edges[0].ExtractedFrom.Should().Be(DocA,
            "**受容する残余**: 起点は先に抽出した側が保持する（IADR-0281 §受容する残余）");
    }

    // ── 辞書が空（seed 前）: 辺を作らない側へ倒す ───────────────────────────

    [Fact]
    public async Task 辺の型辞書が空なら辺を作らない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        db.Documents.Add(GraphDocument.Create(DocB, "文書B",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, "fp-b", T0));
        await db.SaveChangesAsync(ct);
        var (consumer, _, _) = Build(db, "[[文書B]]");

        await consumer.Handle(Event(), ct);

        db.Edges.Should().BeEmpty("存在しない型 ID の辺は後から作れない");
        db.Documents.Count().Should().Be(2, "陽性対照: ノードの同期そのものは行われている");
    }

    // ── 計測の道具（IngestTagFilterTests と同じ作法） ───────────────────────

    // Meter 名へ一意な接尾辞を付ける（テスト間の測定の混入を構造的に防ぐ）。
    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public string? CreatedMeterName { get; private set; }

        public Meter Create(MeterOptions options)
        {
            CreatedMeterName = $"{options.Name}.test-{Guid.NewGuid():N}";
            var meter = new Meter(CreatedMeterName, options.Version, options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
            _meters.Clear();
        }
    }

    private sealed class MetricsProbe
    {
        private readonly List<long> _values = [];
        private readonly List<string> _layers = [];

        public MetricsProbe(string meterName)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName
                        && instrument.Name == EdgeTypeFallbackMetrics.FallbackCounterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var layer = string.Empty;
                foreach (var tag in tags)
                    if (tag.Key == EdgeTypeFallbackMetrics.LayerTag)
                        layer = tag.Value?.ToString() ?? string.Empty;
                lock (_values)
                {
                    _values.Add(value);
                    _layers.Add(layer);
                }
            });
            listener.Start();
        }

        public long Total { get { lock (_values) return _values.Sum(); } }

        public IReadOnlyList<string> Layers { get { lock (_values) return [.. _layers]; } }
    }
}
