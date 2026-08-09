using System.Diagnostics.Metrics;
using DocumentService.Api.Composable.Steps;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Observability;
using DocumentService.Api.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentService.Api.Tests;

// FR-01, UC-04, SC-05, SC-09, SC-10, #637: **取り込み経路で辞書に無いタグが現れたら、
// 文書へ付けずに件数として記録する**（計画確定・2026-08-09。利用者裁定 planning#304）。
//
// **SC-05 の「既定タグ辞書に整合」は経路を問わない不変条件**である。
// **取り込み経路はタグを生成しない**ので、ここが非空になること自体が規定違反であり、
// **0 でない件数が「計画に無い経路でタグが生まれている」ことの検出**になる（SC-10。**0 が正常**）。
public sealed class IngestTagFilterTests
{
    private static DocumentDbContext NewDb() => new(
        new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase($"ingest-tags-{Guid.NewGuid():N}")
            .Options);

    // **Meter 名は一意にする。** `MeterListener` はプロセス全体を購読するため、
    // 固定名だと並行する他テストの測定が混ざる（`CompletionMetricsTests` はコレクションで直列化して
    // これを避けているが、こちらは Meter を毎回作り分けられるので混入自体を起こさない）。
    private static (DocumentNormalizedConsumer Consumer, MetricsProbe Probe) Build(DocumentDbContext db)
    {
        var factory = new TestMeterFactory();
        var metrics = new IngestTagMetrics(factory);
        var probe = new MetricsProbe(factory.CreatedMeterName!);
        var consumer = new DocumentNormalizedConsumer(
            db, new NoopPublishEndpoint(), metrics,
            NullLogger<DocumentNormalizedConsumer>.Instance);
        return (consumer, probe);
    }

    // 規定どおり（取り込みはタグを生成しない）なら、何も起きない。**0 が正常である。**
    [Fact]
    public async Task NoTags_RecordsNothing()
    {
        using var db = NewDb();
        var (consumer, probe) = Build(db);

        var kept = await consumer.KnownTagsAsync([], default);

        kept.Should().BeEmpty();
        probe.Total.Should().Be(0, "規定どおりなら件数は 0 のまま");
    }

    // 辞書に在るタグは通す（将来コネクタがタグを運ぶ場合に、辞書内の値まで落とさない）。
    [Fact]
    public async Task KnownTag_IsKept_AndNotCounted()
    {
        using var db = NewDb();
        var tag = Tag.Create("経理");
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var (consumer, probe) = Build(db);

        var kept = await consumer.KnownTagsAsync(["経理"], default);

        // #635: 通ったタグは**識別子**で返る（正本は表示名を持たない。[[IADR-0153]] 決定 1）。
        kept.Should().Equal([tag.Id]);
        probe.Total.Should().Be(0);
    }

    // **辞書に無いタグは文書へ付けない。** 付けると SC-05 の不変条件が経路をすり抜けて破れる。
    [Fact]
    public async Task UnknownTag_IsDropped_AndCounted()
    {
        using var db = NewDb();
        var (consumer, probe) = Build(db);

        var kept = await consumer.KnownTagsAsync(["決算資料"], default);

        kept.Should().BeEmpty("辞書整合は経路を問わない不変条件である");
        probe.Total.Should().Be(1, "0 でない値が規定違反の検出になる");
    }

    // 混在: 辞書に在るものだけ残り、無いものだけ数える。
    [Fact]
    public async Task MixedTags_KeepsKnown_AndCountsUnknownOnly()
    {
        using var db = NewDb();
        var tag = Tag.Create("規程");
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var (consumer, probe) = Build(db);

        var kept = await consumer.KnownTagsAsync(["規程", "未登録A", "未登録B"], default);

        kept.Should().Equal([tag.Id]);
        probe.Total.Should().Be(2);
    }

    // **同じ未知タグが 2 度来ても 1 件と数える**（#634 の使用件数と同じ理屈。
    // 数えるのは「現れたタグの種類」であって出現回数ではない）。
    [Fact]
    public async Task DuplicateUnknownTag_IsCountedOnce()
    {
        using var db = NewDb();
        var (consumer, probe) = Build(db);

        var kept = await consumer.KnownTagsAsync(["未登録", "未登録", "未登録"], default);

        kept.Should().BeEmpty();
        probe.Total.Should().Be(1, "出現回数ではなく種類を数える");
    }

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

    // `ingest.unknown_tag.total` の測定を収集する（`CompletionMetricsTests` と同じ作法）。
    private sealed class MetricsProbe
    {
        private readonly List<long> _values = [];

        public MetricsProbe(string meterName)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName
                        && instrument.Name == IngestTagMetrics.UnknownTagCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_values) _values.Add(value);
            });
            listener.Start();
        }

        public long Total { get { lock (_values) return _values.Sum(); } }
    }

    // 本テストは絞り込みだけを見る。発行はここでの検証対象ではない。
    private sealed class NoopPublishEndpoint : MassTransit.IPublishEndpoint
    {
        public Task Publish<T>(T message, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext<T>> pipe, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext> pipe, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task Publish(object message, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish(object message, MassTransit.IPipe<MassTransit.PublishContext> pipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, MassTransit.IPipe<MassTransit.PublishContext> pipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<T>(object values, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext<T>> pipe, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext> pipe, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public MassTransit.ConnectHandle ConnectPublishObserver(MassTransit.IPublishObserver observer) => throw new NotSupportedException();
    }
}
