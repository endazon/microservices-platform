using System.Diagnostics.Metrics;
using AwesomeAssertions;
using IngestionService.Common.Observability;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using IngestionService.Features.Ingestion.ReembedPending;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IngestionService.Tests.Features.Ingestion.ReembedPending;

// FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 2・3 (#1908): **埋め込み待ちの点の埋め直し（1 周期）と観測。**
//
// 測ること:
//   - 埋め込めた点は「主へ書く → 語彙索引の待ちの点を消す」の順で移る（二重に出ない・消し忘れない）。
//   - 埋め込めなければ点を残し、その周期を打ち切る（鍵が無い間ゲートウェイを叩き続けない）。
//   - 書く直前に読み直し、取り込みが書き直していたら古い本文を主へ書かない。
//   - 高機密の点は埋め込みを呼ばない（二重の守り）。
//   - 件数をゲージへ記録し、0 でなければ Warning を必ず 1 行出す。0 なら出さない。
[Trait("TestKind", "Unit")]
public class EmbeddingPendingReembedderTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(9));

    private static EmbeddingPendingPoint Point(int chunkIndex, string confidentiality = "internal",
        string text = "本文", Guid? documentId = null)
    {
        var doc = documentId ?? Guid.Parse("22222222-2222-2222-2222-222222222222");
        var id = chunkIndex == ChunkId.MetadataChunkIndex ? ChunkId.DeriveMetadata(doc) : ChunkId.Derive(doc, chunkIndex);
        return new EmbeddingPendingPoint(id, doc, "決算短信", $"{text}{chunkIndex}", chunkIndex,
            chunkIndex == ChunkId.MetadataChunkIndex ? null : "storage://b/x.md",
            new Dictionary<string, string> { ["confidentiality"] = confidentiality, ["project"] = "ai-stock-trading" },
            ["決算"], UpdatedAt, ["bob"], EmbeddingPendingReasons.Transient);
    }

    private static (EmbeddingPendingReembedder Reembedder, EmbeddingPendingMetrics Metrics, CapturingLogger Logger)
        Build(PendingStore store, IEmbeddingService embed, int batchSize = 64)
    {
        var metrics = TestMeterFactory.NewEmbeddingPendingMetrics();
        var logger = new CapturingLogger();
        var reembedder = new EmbeddingPendingReembedder(store, embed, metrics,
            EmbeddingPendingOptions.Default with { BatchSize = batchSize }, IngestionTimeouts.Default, logger);
        return (reembedder, metrics, logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // T-41 **陽性**: 埋め込めた点は主へ同じ ID・同じペイロードで書かれ、その後に語彙索引から消える。
    [Fact]
    public async Task 埋め込めた点は主へ書いてから語彙索引の待ちの点を消す()
    {
        var chunk = Point(0);
        var metadata = Point(ChunkId.MetadataChunkIndex);
        var store = new PendingStore(chunk, metadata);
        var embed = new FixedEmbedding(embedded: true);
        var (reembedder, metrics, logger) = Build(store, embed);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Should().Be(new EmbeddingPendingSweep(2, 2, 0, null));
        store.Operations.Should().Equal(
            $"primary-chunk:{chunk.PointId}", $"delete-pending:{chunk.PointId}",
            $"primary-metadata:{metadata.PointId}", $"delete-pending:{metadata.PointId}");
        store.Pending.Should().BeEmpty("消し忘れない");
        var written = store.PrimaryChunks.Should().ContainSingle().Subject;
        written.Should().BeEquivalentTo(chunk,
            "書いたときの値（本文・属性・タグ・共有先・更新日時・所在）をそのまま運ぶ");
        store.PrimaryMetadata.Should().ContainSingle().Which.PointId.Should().Be(metadata.PointId);
        embed.Requests.Should().OnlyContain(r => r.Confidentiality == "internal");
        metrics.LastPending.Should().Be(0);
        logger.Warnings.Should().BeEmpty("待ちが 0 になった周期は Warning を出さない");
    }

    // T-42 **陰性**: 埋め込めない（鍵がまだ無い）なら主へ書かず、点を消さず、その周期を最初の 1 点で打ち切る。
    [Fact]
    public async Task 埋め込めなければ点を残し周期を打ち切る()
    {
        var store = new PendingStore(Point(0), Point(1), Point(2));
        var embed = new FixedEmbedding(embedded: false, retryable: true);
        var (reembedder, metrics, logger) = Build(store, embed);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Should().Be(new EmbeddingPendingSweep(3, 0, 0, EmbeddingPendingReasons.Transient));
        embed.Requests.Should().ContainSingle("最初の失敗で打ち切る（待ちの点の数だけゲートウェイを叩かない）");
        store.PrimaryChunks.Should().BeEmpty();
        store.Pending.Should().HaveCount(3, "埋め込めなかった点は語彙索引に残る（全文検索では当たり続ける）");
        metrics.LastPending.Should().Be(3);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("3 point(s)").And.Contain("transient");
    }

    // T-43 **陰性（二重の守り）**: 印が付いていても、属性が高機密なら埋め込みを呼ばない（本文をどの送信先へも渡さない）。
    [Theory]
    [InlineData("restricted")]
    [InlineData("confidential")]
    [InlineData("secret")]
    public async Task 高機密の属性を持つ点は埋め込みを呼ばずに残す(string confidentiality)
    {
        var store = new PendingStore(Point(0, confidentiality));
        var embed = new FixedEmbedding(embedded: true);
        var (reembedder, _, logger) = Build(store, embed);

        var sweep = await reembedder.SweepAsync(Ct);

        embed.Requests.Should().BeEmpty("ADR-0127 決定 1: 高機密の本文は埋め込みへ渡さない");
        sweep.HeldBack.Should().Be(1);
        store.PrimaryChunks.Should().BeEmpty();
        store.Pending.Should().ContainSingle();
        logger.Warnings.Should().ContainSingle("残り続けるので毎周期 Warning に出る（覆い隠さない）");
    }

    // T-44 **陰性（取り込みとの競合）**: 書く直前に読み直し、点が消えていた・中身が変わっていたら主へ書かない。
    [Fact]
    public async Task 取り込みが書き直した点は主へ書かない()
    {
        var gone = Point(0);
        var changed = Point(1);
        var rewritten = Point(2);
        var store = new PendingStore(gone, changed, rewritten)
        {
            // 埋め込みの最中に取り込みが同じ文書を書き直した: 1 点目は削除済み、2 点目は更新日時が新しい、
            // 3 点目は更新日時が同じまま本文だけが変わった（#1924 の監査: 本文の比較も固定する）。
            OnGet = (id, current) => id == gone.PointId ? null
                : id == changed.PointId ? current with { UpdatedAt = UpdatedAt.AddMinutes(5) }
                : current with { Text = "書き直した本文" },
        };
        var (reembedder, _, _) = Build(store, new FixedEmbedding(embedded: true));

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Reembedded.Should().Be(0);
        store.PrimaryChunks.Should().BeEmpty("古い本文を主へ書かない");
        store.Operations.Should().NotContain(o => o.StartsWith("delete-pending", StringComparison.Ordinal),
            "取り込みが書いた新しい点を消さない");
    }

    // T-45: 待ちが 0 の周期は一覧も埋め込みも呼ばず、ゲージは 0。Warning は出ない。1 周期の上限を超えては拾わない。
    [Fact]
    public async Task 待ちが無ければ何も呼ばずゲージは0()
    {
        var store = new PendingStore();
        var embed = new FixedEmbedding(embedded: true);
        var (reembedder, metrics, logger) = Build(store, embed);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Should().Be(new EmbeddingPendingSweep(0, 0, 0, null));
        store.ListCalls.Should().Be(0);
        embed.Requests.Should().BeEmpty();
        metrics.LastPending.Should().Be(0);
        logger.Warnings.Should().BeEmpty();
        store.IndexEnsured.Should().BeTrue("最初の周期で印の索引を張る");
    }

    [Fact]
    public async Task 一周期で拾うのは上限まで()
    {
        var store = new PendingStore(Point(0), Point(1), Point(2));
        var (reembedder, metrics, logger) = Build(store, new FixedEmbedding(embedded: true), batchSize: 2);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Reembedded.Should().Be(2);
        store.Pending.Should().ContainSingle();
        metrics.LastPending.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("1 point(s)");
    }

    // T-45（#1924 のレビュー）: 残りの件数は周期の終わりに**数え直す**。周期の途中で取り込みが新しく書いた待ちの点も
    // ゲージと Warning に含める（始めの件数からの引き算だと 0 と出て、新しい待ちが見えない）。
    [Fact]
    public async Task 残りの件数は周期の終わりに数え直し途中で書かれた待ちを含める()
    {
        var late = Point(0, documentId: Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var store = new PendingStore(Point(0));
        // 1 点目を主へ書いた直後に、取り込みが別の文書の待ちの点を書いた。
        store.OnPrimaryWrite = () => store.Pending.Add(late);
        var (reembedder, metrics, logger) = Build(store, new FixedEmbedding(embedded: true), batchSize: 1);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Reembedded.Should().Be(1);
        metrics.LastPending.Should().Be(1, "途中で書かれた待ちの点を数える");
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("1 point(s)");
    }

    // T-43（#1924 の監査）: 高機密として残した点が 1 周期の上限以上あっても、周期は進む（残した点が枠を占めない）。
    [Fact]
    public async Task 高機密として残した点が上限を占めても埋めてよい点を拾う()
    {
        var restricted = Point(0, "restricted");
        var internalPoint = Point(1);
        var store = new PendingStore(restricted, internalPoint);
        var (reembedder, _, _) = Build(store, new FixedEmbedding(embedded: true), batchSize: 1);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Reembedded.Should().Be(1, "1 周期目で埋めてよい点へ届く");
        sweep.HeldBack.Should().Be(1);
        store.PrimaryChunks.Should().ContainSingle().Which.PointId.Should().Be(internalPoint.PointId);
        store.Pending.Should().ContainSingle().Which.PointId.Should().Be(restricted.PointId);
    }

    // T-45（#1924 のレビュー）: 印はあるのに復元できない点は黙って落とさない。件数と ID（数件）を Warning に出し、
    // 枠を占めさせない（数える側は数え続けるので、出さないとゲージが 0 にならない理由が見えない）。
    [Fact]
    public async Task 復元できない点は件数とIDをWarningへ出し枠を占めない()
    {
        var store = new PendingStore(Point(0));
        store.Unreadable.Add("broken-1");
        var (reembedder, metrics, logger) = Build(store, new FixedEmbedding(embedded: true), batchSize: 1);

        var sweep = await reembedder.SweepAsync(Ct);

        sweep.Reembedded.Should().Be(1);
        metrics.LastPending.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Should()
            .Contain("unreadable 1").And.Contain("broken-1");
    }

    // T-45（#1924 の監査）: 埋め直した数を計数 `ingestion.embedding_pending.reembedded` で数える。
    [Fact]
    public async Task 埋め直した数を計数で数える()
    {
        using var factory = new TestMeterFactory();
        var metrics = new EmbeddingPendingMetrics(factory);
        var reembedded = new List<long>();
        using var listener = Listen(factory, EmbeddingPendingMetrics.ReembeddedCounterName, (v, _) => reembedded.Add(v));
        var store = new PendingStore(Point(0), Point(1));
        var reembedder = new EmbeddingPendingReembedder(store, new FixedEmbedding(embedded: true), metrics,
            EmbeddingPendingOptions.Default, IngestionTimeouts.Default, new CapturingLogger());

        await reembedder.SweepAsync(Ct);

        reembedded.Should().Equal(2);
    }

    // T-45（#1924 の監査）: 常駐の器は、周期が例外を出しても Warning を残して次の周期へ進み、ホストを止めない。
    [Fact]
    public async Task 周期が例外を出しても器は次の周期へ進む()
    {
        var store = new PendingStore(Point(0)) { CountFailures = 1 };
        var options = EmbeddingPendingOptions.Default with { Interval = TimeSpan.FromMilliseconds(20) };
        var reembedder = new EmbeddingPendingReembedder(store, new FixedEmbedding(embedded: true),
            TestMeterFactory.NewEmbeddingPendingMetrics(), options, IngestionTimeouts.Default, new CapturingLogger());
        var hostLogger = new CapturingHostLogger();
        var hosted = new EmbeddingPendingReembedHostedService(reembedder, options, TimeProvider.System, hostLogger);

        await hosted.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Pending.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        await hosted.StopAsync(Ct);

        hostLogger.Warnings.Should().ContainSingle().Which.Should().Contain("re-embed cycle failed");
        store.Pending.Should().BeEmpty("次の周期で埋め直した");
        hosted.ExecuteTask!.IsFaulted.Should().BeFalse("例外でホストを止めない");
    }

    internal static MeterListener Listen(TestMeterFactory factory, string instrumentName,
        Action<long, KeyValuePair<string, object?>[]> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => onMeasurement(value, tags.ToArray()));
        listener.Start();
        return listener;
    }

    // T-45: ゲージは数えるまで系列を出さない（0 を出すと「待ちなし」と読める）。数えた後は値を出す。
    [Fact]
    public void ゲージは数えるまで系列を出さない()
    {
        using var factory = new TestMeterFactory();
        var metrics = new EmbeddingPendingMetrics(factory);
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory && instrument.Name == EmbeddingPendingMetrics.PendingGaugeName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed.Add(value));
        listener.Start();

        listener.RecordObservableInstruments();
        observed.Should().BeEmpty();

        metrics.RecordPending(7);
        listener.RecordObservableInstruments();
        observed.Should().Equal(7);
    }

    // T-45: 周期と上限の構成（正の整数でなければ既定へ倒す）。
    [Theory]
    [InlineData(null, null, 300, 64)]
    [InlineData("60", "16", 60, 16)]
    [InlineData("0", "-1", 300, 64)]
    [InlineData("abc", "", 300, 64)]
    public void 構成は正の整数でなければ既定へ倒す(string? interval, string? batch, int expectedSeconds, int expectedBatch)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new(EmbeddingPendingOptions.IntervalKey, interval),
            new(EmbeddingPendingOptions.BatchSizeKey, batch),
        ]).Build();

        var options = EmbeddingPendingOptions.From(config);

        options.Interval.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        options.BatchSize.Should().Be(expectedBatch);
    }

    // ── 器 ─────────────────────────────────────────────

    private sealed class FixedEmbedding(bool embedded, bool retryable = false) : IEmbeddingService
    {
        public List<(string Text, string? Confidentiality)> Requests { get; } = [];

        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        {
            Requests.Add((text, confidentiality));
            return Task.FromResult(new EmbeddingResult(embedded ? new float[4] : [], "knowledge_chunks_voyage_3_5",
                embedded, retryable));
        }
    }

    // 語彙索引の待ちの点を持つ偽物（順序を記録する）。
    private sealed class PendingStore(params EmbeddingPendingPoint[] points) : IIngestionVectorStore
    {
        public List<EmbeddingPendingPoint> Pending { get; } = [.. points];
        public List<EmbeddingPendingPoint> PrimaryChunks { get; } = [];
        public List<EmbeddingPendingPoint> PrimaryMetadata { get; } = [];
        public List<string> Operations { get; } = [];
        public int ListCalls { get; private set; }
        public bool IndexEnsured { get; private set; }

        // 読み直しの結果を差し替える（取り込みとの競合を模す）。既定は今の点をそのまま返す。
        public Func<Guid, EmbeddingPendingPoint, EmbeddingPendingPoint?> OnGet { get; init; } = (_, p) => p;

        // 主へ書いた直後に呼ぶ（周期の途中の取り込みの書き込みを模す）。
        public Action? OnPrimaryWrite { get; set; }

        public Task EnsureEmbeddingPendingIndexAsync(CancellationToken ct = default)
        {
            IndexEnsured = true;
            return Task.CompletedTask;
        }

        // 印はあるのに復元できない点（一覧の先頭に並ぶ。数える側は数える）。
        public List<string> Unreadable { get; } = [];

        // 数える呼び出しで投げる回数（周期の失敗を模す）。
        public int CountFailures { get; set; }
        public int CountCalls { get; private set; }

        public Task<long> CountEmbeddingPendingAsync(CancellationToken ct = default)
        {
            CountCalls++;
            if (CountFailures > 0)
            {
                CountFailures--;
                throw new InvalidOperationException("qdrant unreachable (test)");
            }
            return Task.FromResult((long)(Pending.Count + Unreadable.Count));
        }

        public Task<EmbeddingPendingPage> ListEmbeddingPendingAsync(int limit, CancellationToken ct = default)
        {
            ListCalls++;
            var unreadable = Unreadable.Take(limit).ToList();
            return Task.FromResult(new EmbeddingPendingPage([.. Pending.Take(limit - unreadable.Count)], unreadable));
        }

        public Task<EmbeddingPendingPoint?> GetEmbeddingPendingAsync(Guid pointId, CancellationToken ct = default)
        {
            var current = Pending.FirstOrDefault(p => p.PointId == pointId);
            return Task.FromResult(current is null ? null : OnGet(pointId, current));
        }

        public Task DeleteEmbeddingPendingAsync(Guid pointId, CancellationToken ct = default)
        {
            Operations.Add($"delete-pending:{pointId}");
            Pending.RemoveAll(p => p.PointId == pointId);
            return Task.CompletedTask;
        }

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
        {
            Operations.Add($"primary-chunk:{chunkId}");
            PrimaryChunks.Add(new EmbeddingPendingPoint(chunkId, documentId, title, text, chunkIndex, markdownUri,
                attributes, tags, updatedAt, sharedWith, EmbeddingPendingReasons.Transient));
            OnPrimaryWrite?.Invoke();
            return Task.CompletedTask;
        }

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
        {
            Operations.Add($"primary-metadata:{pointId}");
            PrimaryMetadata.Add(new EmbeddingPendingPoint(pointId, documentId, title, indexText,
                ChunkId.MetadataChunkIndex, markdownUri, attributes, tags, updatedAt, sharedWith,
                EmbeddingPendingReasons.Transient));
            return Task.CompletedTask;
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text, int chunkIndex,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title, string indexText,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingHostLogger : ILogger<EmbeddingPendingReembedHostedService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLogger : ILogger<EmbeddingPendingReembedder>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
