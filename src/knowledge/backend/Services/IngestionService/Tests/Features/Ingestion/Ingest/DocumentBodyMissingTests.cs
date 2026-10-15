using System.Diagnostics.Metrics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using IngestionService.Common.Observability;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;
using Wolverine.Tracking;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02 テスト仕様 T-49〜T-51 (#1931) —— UC-04, [[IADR-0540]]: **本文のオブジェクトが無い（S3 の NoSuchKey）文書は、
// 再試行も DLQ への送り込みもせず、記録だけして正常に終える。**
//
// - T-49（読み手の分類）: NoSuchKey（専用の型・汎用の型にエラーコード）だけを `DocumentBodyMissingException` へ写す。
//   バケットが無い（404 `NoSuchBucket`）・5xx・時間切れ・接続の失敗は写さない（従来どおりの例外のまま）。
// - T-50（受け口）: Warning 1 行（文書 ID・本文のキー・更新日時）・計数 `ingestion.body_missing` が 1・点を書かない・完了を発行しない。
// - T-51（実配送）: 本番と同じ共通既定（`UsePlatformMessagingDefaults`）の Wolverine で、NoSuchKey は 1 回の試行で終わり
//   デッドレターへ行かない。対照: 5xx は従来どおり再試行される。
[Trait("TestKind", "Unit")]
public class DocumentBodyMissingTests
{
    private const string BodyUri = "storage://knowledge/docs/2026/09/old-news.md";
    private static readonly Guid DocId = Guid.Parse("19311931-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 20, 3, 4, 5, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentUpdated Event(string confidentiality = "internal") => new(
        DocId, "切り替え前のニュース", "normalized", BodyUri,
        new Dictionary<string, string> { ["confidentiality"] = confidentiality }, ["news"], UpdatedAt);

    // ---- T-49: 読み手の分類 ----

    public static TheoryData<string> MissingKinds => ["typed", "error-code"];

    [Theory]
    [MemberData(nameof(MissingKinds))]
    public async Task NoSuchKeyは本文の欠落として写す(string kind)
    {
        AmazonS3Exception thrown = kind == "typed"
            ? new NoSuchKeyException("The specified key does not exist.")
            { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" }
            : new AmazonS3Exception("The specified key does not exist.")
            { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
        var reader = Reader(new ScriptedStorage(_ => throw thrown));

        var act = () => reader.ReadAsync(BodyUri, "t", Ct);

        var missing = (await act.Should().ThrowAsync<DocumentBodyMissingException>()).Which;
        missing.MarkdownUri.Should().Be(BodyUri);
        missing.InnerException.Should().BeSameAs(thrown);
    }

    // 🔴 一時障害・構成の不調は本文の欠落へ写さない（再試行へ委ねる）。404 でも `NoSuchBucket` は欠落ではない。
    public static TheoryData<string> TransientKinds => ["no-such-bucket-404", "internal-error-500", "slow-down-503", "timeout", "connection"];

    [Theory]
    [MemberData(nameof(TransientKinds))]
    public async Task 一時障害は本文の欠落へ写さずそのまま上げる(string kind)
    {
        Exception thrown = kind switch
        {
            "no-such-bucket-404" => new AmazonS3Exception("The specified bucket does not exist")
            { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchBucket" },
            "internal-error-500" => new AmazonS3Exception("We encountered an internal error. Please try again.")
            { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" },
            "slow-down-503" => new AmazonS3Exception("Please reduce your request rate.")
            { StatusCode = HttpStatusCode.ServiceUnavailable, ErrorCode = "SlowDown" },
            "timeout" => new TimeoutException("The operation has timed out."),
            _ => new HttpRequestException("Connection refused (seaweedfs-s3:8333)"),
        };
        var reader = Reader(new ScriptedStorage(_ => throw thrown));

        var act = () => reader.ReadAsync(BodyUri, "t", Ct);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task 本文があれば従来どおり返す()
    {
        var reader = Reader(new ScriptedStorage(_ => "# 見出し\n\n本文"));

        (await reader.ReadAsync(BodyUri, "t", Ct)).Should().Be("# 見出し\n\n本文");
    }

    // ---- T-50: 受け口 ----

    [Fact]
    public async Task 本文が無ければ記録だけして正常に終える()
    {
        using var factory = new TestMeterFactory();
        var counted = new List<long>();
        using var listener = Listen(factory, BodyMissingMetrics.CounterName, counted.Add);
        var store = new RecordingStore();
        var completed = new RecordingCompletedPublisher();
        var logger = new WarningLogger();
        var consumer = Consumer(
            Reader(new ScriptedStorage(_ => throw new NoSuchKeyException("The specified key does not exist."))),
            store, completed, new BodyMissingMetrics(factory), logger);

        // 例外を上げない（Wolverine はメッセージを ack する）。
        await consumer.Handle(Event(), Ct);

        counted.Should().Equal(1);
        var warning = logger.Warnings.Should().ContainSingle().Which;
        warning.Should().Contain(DocId.ToString()).And.Contain(BodyUri).And.Contain("2026-09-20T03:04:05")
            .And.Contain("NoSuchKey");
        // 🔴 未決（#1931 項目 3）: 点は 1 つも書かない（題名だけのメタデータ点も書かない）。
        // 冒頭の全コレクション削除は従来どおりの位置で走る（本件はそれを足しも外しもしない）。
        store.Operations.Should().Equal("delete-from-all");
        completed.Published.Should().BeEmpty("取り込みは完了していない");
    }

    // 高機密の文書でも同じ（語彙索引への分岐より前に本文を読む —— 経路は変えない）。
    [Fact]
    public async Task 高機密の文書でも本文が無ければ記録だけして正常に終える()
    {
        var store = new RecordingStore();
        var consumer = Consumer(
            Reader(new ScriptedStorage(_ => throw new NoSuchKeyException("missing"))),
            store, new RecordingCompletedPublisher(), TestMeterFactory.NewBodyMissingMetrics(), new WarningLogger());

        await consumer.Handle(Event("confidential"), Ct);

        store.Operations.Should().Equal("delete-from-all");
    }

    // 対照: 本文があれば従来どおり書き、計数は増えず Warning も出ない。
    [Fact]
    public async Task 本文があれば従来どおり索引し計数は増えない()
    {
        using var factory = new TestMeterFactory();
        var counted = new List<long>();
        using var listener = Listen(factory, BodyMissingMetrics.CounterName, counted.Add);
        var store = new RecordingStore();
        var completed = new RecordingCompletedPublisher();
        var logger = new WarningLogger();
        var consumer = Consumer(Reader(new ScriptedStorage(_ => "# A\n\nまる\n\n# B\n\nばつ")),
            store, completed, new BodyMissingMetrics(factory), logger);

        await consumer.Handle(Event(), Ct);

        store.Operations.Should().Equal("delete-from-all", "upsert", "upsert");
        completed.Published.Should().ContainSingle().Which.ChunkCount.Should().Be(2);
        counted.Should().BeEmpty();
        logger.Warnings.Should().BeEmpty();
    }

    // 対照: 一時障害は従来どおり例外のまま上がる（受け口では握りつぶさない）。
    [Fact]
    public async Task 一時障害は受け口でも例外のまま上げる()
    {
        using var factory = new TestMeterFactory();
        var counted = new List<long>();
        using var listener = Listen(factory, BodyMissingMetrics.CounterName, counted.Add);
        var consumer = Consumer(
            Reader(new ScriptedStorage(_ => throw new AmazonS3Exception("internal")
            { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" })),
            new RecordingStore(), new RecordingCompletedPublisher(), new BodyMissingMetrics(factory), new WarningLogger());

        var act = () => consumer.Handle(Event(), Ct);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        counted.Should().BeEmpty();
    }

    // ---- T-51: 実配送（Wolverine・本番と同じ共通既定） ----

    [Fact]
    public async Task 実配送で本文が無ければ一回の試行で終わりデッドレターへ行かない()
    {
        var storage = new ScriptedStorage(_ => throw new NoSuchKeyException("The specified key does not exist."));
        using var host = BuildHost(storage);
        await host.StartAsync(Ct);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(20))
            .PublishMessageAndWaitAsync(Event());
        await host.StopAsync(Ct);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty("恒久的な欠落は DLQ へ送らない");
        storage.Calls.Should().Be(1, "再試行しない");
        session.MessageSucceeded.SingleMessage<DocumentUpdated>().DocumentId.Should().Be(DocId);
    }

    [Fact]
    public async Task 実配送で5xxは従来どおり再試行される()
    {
        var storage = new ScriptedStorage(call => call == 1
            ? throw new AmazonS3Exception("internal") { StatusCode = HttpStatusCode.InternalServerError, ErrorCode = "InternalError" }
            : "# 見出し\n\n本文");
        using var host = BuildHost(storage);
        await host.StartAsync(Ct);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(Event());
        await host.StopAsync(Ct);

        session.MovedToErrorQueue.Envelopes().Should().BeEmpty();
        storage.Calls.Should().Be(2, "1 回目は 5xx、2 秒後の 2 回目で成功する");
    }

    // ---- 器 ----

    private static StorageDocumentContentReader Reader(IObjectStorageClient storage) =>
        new(new HttpClient(), storage, NullLogger<StorageDocumentContentReader>.Instance);

    private static DocumentUpdatedConsumer Consumer(IDocumentContentReader reader, RecordingStore store,
        RecordingCompletedPublisher completed, BodyMissingMetrics bodyMissing, ILogger<DocumentUpdatedConsumer> logger)
        => new(reader, new MarkdownChunkingService(), new FixedEmbedder(), store, completed,
            ConsumerTimeoutsForTests.Calls(), ConsumerTimeoutsForTests.Timeouts, TimeProvider.System,
            TestMeterFactory.NewEmbeddingPendingMetrics(), bodyMissing, logger);

    private static IHost BuildHost(ScriptedStorage storage)
        => new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<DocumentUpdatedConsumer>(new PipelineOptions());
                opts.UsePlatformMessagingDefaults();
                opts.PublishMessage<DocumentUpdated>().ToLocalQueue("ingest-body-missing-probe");
                opts.Policies.Add(new EmbeddingBudgetDeadLetterPolicy());
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddPlatformConsumerTimeouts()
                .AddMetrics()
                .AddSingleton<EmbeddingPendingMetrics>()
                .AddSingleton<BodyMissingMetrics>()
                .AddSingleton(IngestionTimeouts.Default)
                .AddSingleton(TimeProvider.System)
                .AddSingleton<IDocumentContentReader>(Reader(storage))
                .AddSingleton<IChunkingService, MarkdownChunkingService>()
                .AddSingleton<IEmbeddingService, FixedEmbedder>()
                .AddSingleton<IIngestionVectorStore, RecordingStore>()
                .AddSingleton<IIngestionCompletedPublisher, RecordingCompletedPublisher>()
                .DisableAllExternalWolverineTransports())
            .Build();

    private static MeterListener Listen(TestMeterFactory factory, string name, Action<long> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory && instrument.Name == name)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => onMeasurement(value));
        listener.Start();
        return listener;
    }

    private sealed class ScriptedStorage(Func<int, string> onGet) : IObjectStorageClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<string> GetTextAsync(string uri, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(onGet(call));
        }

        public bool CanResolve(string? uri) => uri?.StartsWith("storage://", StringComparison.Ordinal) == true;

        public Task<string> PutTextAsync(string key, string text, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => throw new NotSupportedException();
    }

    private sealed class FixedEmbedder : IEmbeddingService
    {
        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
            => Task.FromResult(new EmbeddingResult(new float[4], "knowledge_chunks_test", true));
    }

    private sealed class WarningLogger : ILogger<DocumentUpdatedConsumer>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // 書き込みの種類と順序だけを記録する（どの口で書いても Operations に残る —— 「何も書かない」を測る）。
    private sealed class RecordingStore : IIngestionVectorStore
    {
        public List<string> Operations { get; } = [];

        private Task Record(string op)
        {
            lock (Operations) Operations.Add(op);
            return Task.CompletedTask;
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Record("upsert");

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
            string title, string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Record("metadata-upsert");

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Record("lexical-upsert");

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Record("lexical-metadata-upsert");

        public Task UpsertEmbeddingPendingChunkAsync(Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, CancellationToken ct = default)
            => Record("pending-upsert");

        public Task UpsertEmbeddingPendingMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, CancellationToken ct = default)
            => Record("pending-metadata-upsert");

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
            => Record("delete-from-all");
    }
}
