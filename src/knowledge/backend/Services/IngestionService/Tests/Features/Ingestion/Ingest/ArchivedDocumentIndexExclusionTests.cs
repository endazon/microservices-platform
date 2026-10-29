using IngestionService.Common.Concurrency;
using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-06, FR-02, UC-03, 計画 ADR-0141 決定 2・3, [[IADR-0551]] 決定 2 (#1961): **取り込みのアーカイブの門。**
//
// アーカイブした文書（status=archived の `DocumentUpdated`）は検索・RAG の索引に載せない。既に載っている点は全索引から消す。
// 本文は読まない（一括の対象は本文が無い。本文の在るアーカイブ済みも同じに外れる）。
//
// 🔴 **陰性（載らない）の主張には陽性対照を対で置く**（`PrivateNoteIndexProductionTests` と同じ作法）。索引が空だっただけの緑にしない。
[Trait("TestKind", "Unit")]
public class ArchivedDocumentIndexExclusionTests
{
    private static readonly Guid DocId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DocumentUpdated Event(string status, Guid? id = null, string? markdownUri = "storage://b/doc.md")
        => new(id ?? DocId, "運用手順", status, markdownUri,
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], DateTimeOffset.UtcNow);

    private static (DocumentUpdatedConsumer Consumer, RecordingIndex Index, CountingReader Reader) Build()
    {
        var index = new RecordingIndex();
        var reader = new CountingReader("# 見出し\n\n運用手順の本文");
        var consumer = new DocumentUpdatedConsumer(
            reader,
            new MarkdownChunkingService(),
            new StubEmbedder(),
            index,
            new NoopCompleted(),
            ConsumerTimeoutsForTests.Calls(),
            ConsumerTimeoutsForTests.Timeouts,
            TimeProvider.System,
            TestMeterFactory.NewEmbeddingPendingMetrics(),
            TestMeterFactory.NewBodyMissingMetrics(),
            NullLogger<DocumentUpdatedConsumer>.Instance, new DocumentIndexLocks(), QdrantBootstrapStateForTests.Ensured());
        return (consumer, index, reader);
    }

    // 🔴 アーカイブ済みは点を書かず、本文も読まない。陽性対照: 同じ器・同じ本文で、公開済みの文書は索引される。
    [Fact]
    public async Task アーカイブ済みの文書は索引されず本文も読まれない_公開済みは索引される()
    {
        var (consumer, index, reader) = Build();
        var publishedId = Guid.NewGuid();

        await consumer.Handle(Event(DocumentArchival.ArchivedStatus), TestContext.Current.CancellationToken);
        reader.Reads.Should().Be(0, "アーカイブの門は本文を読む前に置く（埋め込みの費用もかからない）");

        await consumer.Handle(Event("published", publishedId), TestContext.Current.CancellationToken);

        index.Chunks.Should().NotContain(c => c.DocumentId == DocId, "アーカイブした文書は検索・RAG に載せない（ADR-0141 決定 2）");
        index.Chunks.Should().Contain(c => c.DocumentId == publishedId, "陽性対照: 公開済みの文書は従来どおり索引される");
        reader.Reads.Should().Be(1, "陽性対照: 公開済みの文書の本文は読まれる");
    }

    // 🔴 公開済み → アーカイブは、点を残して隠すのではなく**全索引から消す**（`RecordingIndex` は削除を実際に反映する）。
    [Fact]
    public async Task アーカイブすると載っていた点が全索引から消える()
    {
        var (consumer, index, _) = Build();

        await consumer.Handle(Event("published"), TestContext.Current.CancellationToken);
        index.Chunks.Should().Contain(c => c.DocumentId == DocId, "陽性対照: いったんは索引に載っている");

        await consumer.Handle(Event(DocumentArchival.ArchivedStatus), TestContext.Current.CancellationToken);

        index.Chunks.Should().NotContain(c => c.DocumentId == DocId, "アーカイブは索引からの削除まで及ぶ");
        index.Deleted.Should().Contain(DocId);
    }

    // 本文の参照が無いアーカイブ済み（#1931 の一括の対象と同じく本文が無い）も撤収する —— 門は `MarkdownUri` の判定より前にある。
    [Fact]
    public async Task 本文の参照が無いアーカイブ済みも全索引から消す()
    {
        var (consumer, index, _) = Build();

        await consumer.Handle(Event(DocumentArchival.ArchivedStatus, markdownUri: null), TestContext.Current.CancellationToken);

        index.Deleted.Should().Contain(DocId, "撤収は本文の所在に依存しない（後ろに置くと、本文の無い文書の点が残る）");
        index.Chunks.Should().BeEmpty();
    }

    // 戻し方 (a)（再正規化で状態が `normalized` に戻る）では、通常の経路で索引に戻る（ADR-0141 決定 1 の戻し方の下流）。
    [Fact]
    public async Task アーカイブ済みから正規化済みへ戻ると索引に戻る()
    {
        var (consumer, index, _) = Build();

        await consumer.Handle(Event(DocumentArchival.ArchivedStatus), TestContext.Current.CancellationToken);
        await consumer.Handle(Event("normalized"), TestContext.Current.CancellationToken);

        index.Chunks.Should().Contain(c => c.DocumentId == DocId);
    }

    private sealed class RecordingIndex : IIngestionVectorStore
    {
        private readonly List<(Guid DocumentId, string Text)> _chunks = [];

        public IReadOnlyList<(Guid DocumentId, string Text)> Chunks => _chunks;
        public List<Guid> Deleted { get; } = [];

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default)
        {
            _chunks.Add((documentId, text));
            return Task.CompletedTask;
        }

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
            string title, string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default)
        {
            _chunks.Add((documentId, indexText));
            return Task.CompletedTask;
        }

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default)
        {
            _chunks.Add((documentId, text));
            return Task.CompletedTask;
        }

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default)
        {
            _chunks.Add((documentId, indexText));
            return Task.CompletedTask;
        }

        // #2101 / IADR-0595: 版を持たない偽物（印は常に無い ＝ 毎回「判定軸は不明」として先に全消しする。従前の形）。
        public Task<DocumentIndexVersion?> GetIndexVersionAsync(Guid documentId, CancellationToken ct = default)
            => Task.FromResult<DocumentIndexVersion?>(null);

        public Task SetIndexVersionAsync(Guid documentId, DocumentIndexVersion version, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteSupersededAsync(Guid documentId, DateTimeOffset version, long? sequence,
            IReadOnlyCollection<IndexedPointRef> keep, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        {
            Deleted.Add(documentId);
            _chunks.RemoveAll(c => c.DocumentId == documentId);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingReader(string markdown) : IDocumentContentReader
    {
        public int Reads { get; private set; }

        public Task<string> ReadAsync(string uri, string title, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(markdown);
        }
    }

    private sealed class StubEmbedder : IEmbeddingService
    {
        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality,
            CancellationToken ct = default)
            => Task.FromResult(new EmbeddingResult([0.1f, 0.2f], "kb_chunks_test", true));
    }

    private sealed class NoopCompleted : IIngestionCompletedPublisher
    {
        public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
