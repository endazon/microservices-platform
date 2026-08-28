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
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-17, FR-05, FR-18, ADR-0033 決定 2・10 (#911): DocumentUpdated 購読による ABAC 属性の
// デノーマライズと、本文指紋（ADR-0050）による却下済み AI 提案の解除。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く**（GraphTraversalTests と同じ作法）。
public class GraphDocumentSyncConsumerTests
{
    private static readonly Guid DocA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000aa");
    private static readonly Guid DocB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000bb");
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"sync_{Guid.NewGuid():N}").Options);

    // #912: リンク抽出の依存を足した。**本テストの射程は属性同期と却下解除である** ——
    // 本文は取得できない（null）ものとし、抽出は毎回スキップされる。抽出そのものの試験は
    // LinkEdgeSyncTests が受け持つ。
    private static GraphDocumentSyncConsumer Consumer(GraphDbContext db)
        => new(db, new FixedClock(T0.AddDays(10)), new NoBodyReader(),
            new LinkEdgeSynchronizer(db, new EdgeTypeFallbackMetrics(new DummyMeterFactory()),
                NullLogger<LinkEdgeSynchronizer>.Instance),
            NullLogger<GraphDocumentSyncConsumer>.Instance);

    // 本文が取れない（ストレージ未配備）。**辺を一切触らない**側の縮退。
    private sealed class NoBodyReader : IGraphContentReader
    {
        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter($"{options.Name}.test-{Guid.NewGuid():N}", options.Version,
                options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
            _meters.Clear();
        }
    }

    // 解除時刻を固定するテスト用の時計（TimeProvider の最小実装）。
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DocumentUpdated Event(
        Guid? docId = null,
        string confidentiality = "internal",
        DateTimeOffset? updatedAt = null,
        string? fingerprint = "fp-1",
        string title = "文書A")
        => new(docId ?? DocA, title, "published", "storage://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            ["ops"], updatedAt ?? T0, fingerprint);

    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDocument? NodeOf(GraphDbContext db, Guid id)
        => db.Documents.FirstOrDefault(d => d.DocumentId == id);

    // ── デノーマライズ（ADR-0033 決定 2）と AbacNodeFilter の実効 ──────────────

    [Fact]
    public async Task 初回同期でノードが作られAbac判定が複製属性で機能する()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();

        await Consumer(db).Handle(Event(confidentiality: "internal"), ct);

        var node = NodeOf(db, DocA);
        node.Should().NotBeNull();
        // 陽性対照: internal スコープの主体からは可視。
        AbacNodeFilter.Matches(node!, InternalOnly()).Should().BeTrue();
    }

    [Fact]
    public async Task 厳格化イベント適用後は直ちに不可視になる()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await Consumer(db).Handle(Event(confidentiality: "internal", updatedAt: T0), ct);
        AbacNodeFilter.Matches(NodeOf(db, DocA)!, InternalOnly())
            .Should().BeTrue("陽性対照: 厳格化前は可視でなければ、下の否定形は何も測っていない");

        // 厳格化（internal → restricted）。
        await Consumer(db).Handle(
            Event(confidentiality: "restricted", updatedAt: T0.AddMinutes(1)), ct);

        AbacNodeFilter.Matches(NodeOf(db, DocA)!, InternalOnly())
            .Should().BeFalse("厳格化イベント適用後、当該ノードは直ちに不可視になる（#911 受け入れ基準）");
    }

    // ── 順序ガード（追い越し・再配信。IADR-0242 決定 12-4） ─────────────────────

    [Fact]
    public async Task 保持中より古いイベントは適用されない_厳格化後に緩和が復活しない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        // 厳格化（新しい）が先に届く。
        await Consumer(db).Handle(
            Event(confidentiality: "restricted", updatedAt: T0.AddMinutes(5)), ct);

        // 緩和（古い）が遅れて届く —— 適用してはならない。
        await Consumer(db).Handle(
            Event(confidentiality: "internal", updatedAt: T0), ct);

        NodeOf(db, DocA)!.Attributes["confidentiality"].Should().Be("restricted",
            "追い越しで厳格化後に緩和が復活する事故を塞ぐ（保持中より古いイベントは適用しない）");
    }

    [Fact]
    public async Task 同一イベントの再配信は結果を変えない_冪等()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var ev = Event();
        await Consumer(db).Handle(ev, ct);
        await Consumer(db).Handle(ev, ct);

        db.Documents.Count(d => d.DocumentId == DocA).Should().Be(1);
        NodeOf(db, DocA)!.BodyHash.Should().Be("fp-1");
    }

    // ── 却下解除（ADR-0033 決定 10 / ADR-0050 決定 1・2 / #914 の発火側） ────────

    private static async Task<AiSuggestion> SeedRejectedAsync(
        GraphDbContext db, string sourceFpAtRejection, CancellationToken ct)
    {
        var type = EdgeType.Create("cites", EdgeTypeLayer.Core, isSymmetric: false, isSeed: true);
        db.EdgeTypes.Add(type);
        db.Documents.AddRange(
            GraphDocument.Create(DocA, "文書A",
                new() { ["confidentiality"] = "internal" }, sourceFpAtRejection, T0),
            GraphDocument.Create(DocB, "文書B",
                new() { ["confidentiality"] = "internal" }, "fp-b", T0));
        var s = AiSuggestion.CreateLink(DocA, DocB, type.Id, "根拠", T0);
        s.TryReject(sourceFpAtRejection, "fp-b", T0).Should().BeTrue();
        db.AiSuggestions.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    [Fact]
    public async Task 本文指紋が変わると却下済み提案がpendingへ戻る()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var s = await SeedRejectedAsync(db, sourceFpAtRejection: "fp-1", ct);

        // 本文変更（fp-1 → fp-2）。UpdatedAt も進むが、判定に使うのは指紋のみである。
        await Consumer(db).Handle(
            Event(updatedAt: T0.AddMinutes(1), fingerprint: "fp-2"), ct);

        db.AiSuggestions.Single(x => x.Id == s.Id).State.Should().Be(SuggestionState.Pending,
            "両端いずれかの本文が変更された時点で却下を解除し、再提案を許す（ADR-0033 決定 10）");
        db.AiSuggestions.Single(x => x.Id == s.Id).ReinstatedReason.Should().Be("source");
    }

    [Fact]
    public async Task 本文が変わらない更新では却下が解除されない_UpdatedAtでは判定しない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var s = await SeedRejectedAsync(db, sourceFpAtRejection: "fp-1", ct);

        // メタデータだけの更新: UpdatedAt は進むが指紋は同じ。
        await Consumer(db).Handle(
            Event(confidentiality: "restricted", updatedAt: T0.AddMinutes(1), fingerprint: "fp-1"), ct);

        db.AiSuggestions.Single(x => x.Id == s.Id).State.Should().Be(SuggestionState.Rejected,
            "解除の判定は指紋の変化のみで行い、UpdatedAt は用いない（ADR-0050 決定 2）");
    }

    [Fact]
    public async Task 指紋が不明_nullのイベントでは却下が解除されない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var s = await SeedRejectedAsync(db, sourceFpAtRejection: "fp-1", ct);

        await Consumer(db).Handle(
            Event(updatedAt: T0.AddMinutes(1), fingerprint: null), ct);

        db.AiSuggestions.Single(x => x.Id == s.Id).State.Should().Be(SuggestionState.Rejected,
            "指紋 null は「不明」であり、本文が変わった証拠にならない（誤発火させない側に倒す）");
    }

    [Fact]
    public async Task 古いイベントでは却下が解除されない_順序ガードが先に効く()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        var s = await SeedRejectedAsync(db, sourceFpAtRejection: "fp-1", ct);

        // 指紋は違うがイベントが古い（保持 T0 より前）→ 適用されず、解除も起きない。
        await Consumer(db).Handle(
            Event(updatedAt: T0.AddMinutes(-1), fingerprint: "fp-0"), ct);

        db.AiSuggestions.Single(x => x.Id == s.Id).State.Should().Be(SuggestionState.Rejected);
        NodeOf(db, DocA)!.BodyHash.Should().Be("fp-1", "古いイベントは属性・指紋とも適用しない");
    }
}
