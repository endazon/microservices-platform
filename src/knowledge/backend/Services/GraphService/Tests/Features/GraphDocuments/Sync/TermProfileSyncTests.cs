using System.Diagnostics.Metrics;
using AwesomeAssertions;
using GraphService.Common.Observability;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.GraphDocuments.Delete;
using GraphService.Features.GraphDocuments.Sync;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.GraphDocuments.Sync;

// FR-18, ADR-0050 決定 3, IADR-0380 (#1244): 語の出現数（類似度候補の材料）の作成契機と掃除。
//
//   T-51 指紋が変わると本文から作り直す／変わらなければ本文を読まず出現数も変わらない（対）
//   T-52 DocumentDeleted で出現数の行も消える
[Trait("TestKind", "Unit")]
public class TermProfileSyncTests
{
    private static readonly Guid DocA = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000e1");
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static GraphDbContext NewDb() => new(
        new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"tp_{Guid.NewGuid():N}").Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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

    private static (GraphDocumentSyncConsumer Consumer, StubReader Reader) Build(GraphDbContext db, string? body)
    {
        var reader = new StubReader(body);
        var consumer = new GraphDocumentSyncConsumer(
            db, new FixedClock(T0.AddDays(1)), reader,
            new LinkEdgeSynchronizer(db, new EdgeTypeFallbackMetrics(new DummyMeterFactory()),
                NullLogger<LinkEdgeSynchronizer>.Instance),
            new TermProfileSynchronizer(db),
            NullLogger<GraphDocumentSyncConsumer>.Instance);
        return (consumer, reader);
    }

    private static DocumentUpdated Event(string? fingerprint, DateTimeOffset at, string title = "ABAC 判定の設計")
        => new(DocA, title, "published", "storage://b/a.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], at, fingerprint);

    // T-51 陽性: 初回（指紋 null → fp-1）は本文を読み、本文の語が入る。
    [Fact]
    public async Task 指紋が変わると本文から出現数が作られる()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        var (consumer, reader) = Build(db, "ホップごとに認可述語を評価する。");

        await consumer.Handle(Event("fp-1", T0), ct);

        reader.Calls.Should().Be(1);
        var profile = await db.TermProfiles.SingleAsync(p => p.DocumentId == DocA, ct);
        profile.BodyHash.Should().Be("fp-1");
        profile.Terms.Should().ContainKey("認可", "本文の語");
        profile.Terms.Should().ContainKey("abac", "表題の語");
    }

    // T-51 陰性（対）: 指紋が同じなら本文を読まず、出現数も変わらない。
    [Fact]
    public async Task 指紋が同じなら本文を読まず出現数も変わらない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        var (consumer, reader) = Build(db, "ホップごとに認可述語を評価する。");
        await consumer.Handle(Event("fp-1", T0), ct);
        var before = (await db.TermProfiles.AsNoTracking().SingleAsync(p => p.DocumentId == DocA, ct)).Terms;

        // 本文を差し替えても、指紋が同じなら読まれない（＝出現数も差し替わらない）。
        reader.Body = "まったく別の本文。予算と旅費の話。";
        await consumer.Handle(Event("fp-1", T0.AddMinutes(1)), ct);

        reader.Calls.Should().Be(1, "2 回目は読まない");
        var after = (await db.TermProfiles.AsNoTracking().SingleAsync(p => p.DocumentId == DocA, ct)).Terms;
        after.Should().BeEquivalentTo(before);
        after.Should().NotContainKey("予算");

        // 陽性対照: 指紋が進めば差し替わる。
        await consumer.Handle(Event("fp-2", T0.AddMinutes(2)), ct);
        reader.Calls.Should().Be(2);
        (await db.TermProfiles.AsNoTracking().SingleAsync(p => p.DocumentId == DocA, ct))
            .Terms.Should().ContainKey("予算");
    }

    // 縮退: 本文が取れなくても表題だけで出現数ができる（辺と違い、消えるものが無い）。
    [Fact]
    public async Task 本文が取れなければ表題だけで出現数ができる()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        var (consumer, _) = Build(db, body: null);

        await consumer.Handle(Event("fp-1", T0), ct);

        var profile = await db.TermProfiles.SingleAsync(p => p.DocumentId == DocA, ct);
        profile.Terms.Should().ContainKey("abac");
        profile.Terms.Should().NotBeEmpty();
    }

    // 既存文書の初回: 指紋不明（null）でも、出現数の行が無ければ表題から作る（本文は読まない）。
    [Fact]
    public async Task 指紋不明でも出現数の行が無ければ表題から作る_本文は読まない()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        var (consumer, reader) = Build(db, "本文");

        await consumer.Handle(Event(fingerprint: null, T0), ct);

        reader.Calls.Should().Be(0);
        (await db.TermProfiles.SingleAsync(p => p.DocumentId == DocA, ct)).Terms.Should().ContainKey("abac");
    }

    // T-52: DocumentDeleted で出現数の行も消える（陽性対照: 削除前は在る）。
    [Fact]
    public async Task 削除で出現数の行も消える()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = NewDb();
        await EdgeTypeSeed.EnsureSeededAsync(db, ct);
        var (consumer, _) = Build(db, "本文");
        await consumer.Handle(Event("fp-1", T0), ct);
        db.TermProfiles.Count(p => p.DocumentId == DocA).Should().Be(1, "陽性対照");

        await new DocumentDeletedConsumer(db, NullLogger<DocumentDeletedConsumer>.Instance)
            .Handle(new DocumentDeleted(DocA, T0.AddDays(2)), ct);

        db.TermProfiles.Count(p => p.DocumentId == DocA).Should().Be(0);
    }
}
