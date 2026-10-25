using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
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

// 🔴 FR-17, FR-19, IADR-0639 (#2150 / #2251) 段 2: **グラフの順序の門と墓石。** 作業仕様書の規則 11 の表の P1〜P5 を 1 対 1 で写す。
//
// 鍵は辞書順 (時刻, 連番)、削除は (DeletedAt, Sequence)。時刻は保存の精度（マイクロ秒）へ丸めて比べる。
// 時刻には**マイクロ秒より細かい端数**（+3 ティック）を持たせる —— 丸めを外すと、同じ時刻の古い版を端数の分だけ新しいと読み、P1・P3 が赤になる。
// 変異: 辞書順を「連番が先」にすると P2・P5 が、「時刻だけ」にすると P1・P3 が赤になる（作業仕様書の変異の表）。
[Trait("TestKind", "Unit")]
public class GraphSyncOrderingTests
{
    private static readonly Guid Doc = Guid.Parse("aaaaaaaa-2150-0000-0000-0000000000aa");

    // マイクロ秒より細かい端数つきの時刻（丸めの変異を捕まえる）。
    private static readonly DateTimeOffset T = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).AddTicks(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>().UseInMemoryDatabase($"order_{Guid.NewGuid():N}").Options);

    private static GraphDocumentSyncConsumer Updates(GraphDbContext db)
        => new(db, TimeProvider.System, new NoBodyReader(),
            new LinkEdgeSynchronizer(db, new EdgeTypeFallbackMetrics(new DummyMeterFactory()), NullLogger<LinkEdgeSynchronizer>.Instance),
            new TermProfileSynchronizer(db), TagEdgesForTests.Synchronizer(db),
            ConsumerTimeoutsForTests.Calls(), GraphSyncTimeouts.Default,
            NoOpTagEdgeLocks.Instance, NullLogger<GraphDocumentSyncConsumer>.Instance);

    private static DocumentDeletedConsumer Deletes(GraphDbContext db)
        => new(db, TagEdgesForTests.Links(db), TagEdgesForTests.Synchronizer(db), NoOpTagEdgeLocks.Instance,
            NullLogger<DocumentDeletedConsumer>.Instance);

    private static DocumentUpdated On(DateTimeOffset at, long? sequence, string state = "on")
        => new(Doc, $"文書-{state}", "published", "storage://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["state"] = state },
            [], at, "fp-1", Sequence: sequence);

    private static DocumentUpdated Off(DateTimeOffset at, long? sequence)
        => On(at, sequence, "off") with { Attributes = DocumentExposure.WithAllExcluded(new Dictionary<string, string> { ["confidentiality"] = "internal" }) };

    private static GraphDocument? Node(GraphDbContext db) => db.Documents.AsNoTracking().FirstOrDefault(d => d.DocumentId == Doc);

    private static string? State(GraphDbContext db) => Node(db)?.Attributes.GetValueOrDefault("state");

    [Fact]
    public async Task P1_同時刻の逆転_連番の小さい古い版は捨てる()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 6, "new"), Ct);

        await Updates(db).Handle(On(T, 5, "old"), Ct);

        State(db).Should().Be("new", "同じ時刻で連番が小さいのは古い版（#2150）");
    }

    [Fact]
    public async Task P1_陽性対照_同時刻で連番が大きい新しい版は反映する()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 5, "old"), Ct);

        await Updates(db).Handle(On(T, 6, "new"), Ct);

        State(db).Should().Be("new");
    }

    [Fact]
    public async Task P2_作り直し_削除の後に同じIDで作り直された文書は連番が小さくても反映する()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 8), Ct);
        await Deletes(db).Handle(new DocumentDeleted(Doc, T.AddSeconds(1), Sequence: 9), Ct);
        Node(db).Should().BeNull("前提: 削除した");

        await Updates(db).Handle(On(T.AddSeconds(2), 1, "recreated"), Ct);

        State(db).Should().Be("recreated", "作り直しは削除より新しい時刻を持つ（連番は 0 から始まる）");
    }

    [Fact]
    public async Task P3_撤収の後に遅れて届いた古いONはノードを作り直さない()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 5), Ct);
        await Updates(db).Handle(Off(T, 6), Ct);
        Node(db).Should().BeNull("前提: 撤収した");

        await Updates(db).Handle(On(T, 5), Ct);

        Node(db).Should().BeNull("撤収の後に古い ON が後着しても載り直さない（#2251）");
    }

    [Fact]
    public async Task P3_遅れて届いた古いOFFは新しいノードを撤収しない()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 6, "new"), Ct);

        await Updates(db).Handle(Off(T, 5), Ct);

        State(db).Should().Be("new", "古い撤収で新しい版を消さない");
    }

    [Fact]
    public async Task P4_印に連番が無く後着に連番があれば時刻だけで比べ_同時刻は反映する()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, null, "legacy"), Ct);

        await Updates(db).Handle(On(T, 3, "sequenced"), Ct);

        State(db).Should().Be("sequenced");
    }

    [Fact]
    public async Task P4_印に連番があり後着に連番が無ければ時刻だけで比べ_同時刻は反映する()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 3, "sequenced"), Ct);

        await Updates(db).Handle(On(T, null, "legacy"), Ct);

        State(db).Should().Be("legacy", "旧形式の電文（連番なし）は現行どおり時刻で比べる");
    }

    [Fact]
    public async Task P4_時刻が古ければ連番が大きくても捨てる()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 3, "new"), Ct);

        await Updates(db).Handle(On(T.AddMilliseconds(-1), 9, "old"), Ct);

        State(db).Should().Be("new");
    }

    [Fact]
    public async Task P5_削除の後に遅れて届いた古い更新はノードを作り直さない()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 8), Ct);
        await Deletes(db).Handle(new DocumentDeleted(Doc, T, Sequence: 9), Ct);

        await Updates(db).Handle(On(T, 8), Ct);
        await Updates(db).Handle(On(T.AddSeconds(-1), 7), Ct);

        Node(db).Should().BeNull("削除と同じ時刻・それより前の更新は古い");
    }

    [Fact]
    public async Task P5_作り直しの後に遅れて届いた古い削除は適用しない()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T.AddSeconds(2), 1, "recreated"), Ct);

        await Deletes(db).Handle(new DocumentDeleted(Doc, T.AddSeconds(1), Sequence: 9), Ct);

        State(db).Should().Be("recreated", "作り直しより前の削除で、作り直した文書を消さない");
    }

    [Fact]
    public async Task P5_陽性対照_更新の後の削除は適用し墓石が残る()
    {
        using var db = NewDb();
        await Updates(db).Handle(On(T, 8), Ct);

        await Deletes(db).Handle(new DocumentDeleted(Doc, T.AddSeconds(1), Sequence: 9), Ct);

        Node(db).Should().BeNull();
        var mark = db.SyncMarks.AsNoTracking().Single(m => m.DocumentId == Doc);
        mark.Deleted.Should().BeTrue();
        mark.AppliedSequence.Should().Be(9);
    }

    private sealed class NoBodyReader : IGraphContentReader
    {
        public Task<string?> ReadAsync(string? markdownUri, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class DummyMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new($"{options.Name}.test-{Guid.NewGuid():N}", options.Version, options.Tags, scope: this);

        public void Dispose() { }
    }
}
