using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-19, FR-02, ADR-0061 決定 1・2・3・4・5, [[IADR-0394]] 決定 4・5 (#1184):
// **索引の生産側の門**（露出 3 トグルの評価と、OFF へ戻したときの撤収）。
//
// 🔴 **陰性（載らない）の主張には陽性対照を対で置く。** 「索引が空だった」は
// 取り込みが丸ごと壊れていても真になるため、同じテストの中で**載るはずのものが載っている**
// ことを示す（`AbsenceClaims` の作法）。
[Trait("TestKind", "Unit")]
public class PrivateNoteIndexProductionTests
{
    private static readonly Guid NoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // 個人資料の文書属性（`PrivateNoteDefaults` ＋ 露出の投影）。
    private static Dictionary<string, string> PrivateNoteAttributes(
        bool search = false, bool graph = false, bool ai = false)
    {
        var attributes = new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = "alice",
            ["confidentiality"] = "restricted",
        };
        foreach (var (key, value) in DocumentExposure.Project(search, graph, ai))
            attributes[key] = value;
        return attributes;
    }

    private static DocumentUpdated Event(Dictionary<string, string> attributes,
        Guid? id = null, List<string>? sharedWith = null)
        => new(id ?? NoteId, "個人メモ", "normalized",
            "https://storage.example/docs/note.md", attributes, ["memo"],
            DateTimeOffset.UtcNow, SharedWith: sharedWith);

    private static (DocumentUpdatedConsumer Consumer, RecordingIndex Index) Build()
    {
        var index = new RecordingIndex();
        var consumer = new DocumentUpdatedConsumer(
            new StubReader("# 見出し\n\n個人メモの本文"),
            new MarkdownChunkingService(),
            new StubEmbedder(),
            index,
            new NoopCompleted(),
            NullLogger<DocumentUpdatedConsumer>.Instance);
        return (consumer, index);
    }

    // 受け入れ基準 1: 3 トグルとも OFF の個人資料はチャンクが 1 件も作られない。
    // **陽性対照**: 同じ器・同じ本文で、組織文書は従来どおり索引される。
    [Fact]
    public async Task 露出が全てOFFの個人資料は索引されない_組織文書は索引される()
    {
        var (consumer, index) = Build();
        var organizationId = Guid.NewGuid();

        await consumer.Handle(Event(PrivateNoteAttributes()),
            TestContext.Current.CancellationToken);
        await consumer.Handle(
            Event(new Dictionary<string, string> { ["confidentiality"] = "internal" },
                organizationId),
            TestContext.Current.CancellationToken);

        index.Chunks.Should().NotContain(c => c.DocumentId == NoteId,
            "3 つとも OFF なら索引に載せない（ADR-0061 決定 2）");
        index.Chunks.Should().Contain(c => c.DocumentId == organizationId,
            "陽性対照: 組織文書は露出キーを持たず、従来どおり索引される（索引が空なだけの緑にしない）");
    }

    // 受け入れ基準 2: 1 つでも ON なら索引へ載り、判定軸（`doc_scope` / `owner` / `shared_with`）と
    // 3 トグルの投影が点に載る。
    [Fact]
    public async Task 横断検索がONの個人資料は判定軸を載せて索引される()
    {
        var (consumer, index) = Build();

        await consumer.Handle(
            Event(PrivateNoteAttributes(search: true), sharedWith: ["bob"]),
            TestContext.Current.CancellationToken);

        var chunk = index.Chunks.Should().ContainSingle().Subject;
        chunk.Attributes.Should().Contain(DocumentScopes.Key, DocumentScopes.PrivateNote);
        chunk.Attributes.Should().Contain("owner", "alice");
        chunk.Attributes.Should().Contain(DocumentExposure.SearchKey, DocumentExposure.Included);
        chunk.Attributes.Should().Contain(DocumentExposure.GraphKey, DocumentExposure.Excluded);
        chunk.Attributes.Should().Contain(DocumentExposure.AiKey, DocumentExposure.Excluded);
        chunk.SharedWith.Should().BeEquivalentTo(["bob"],
            "`shared_with` が点に載らないと、共有先ベースの分岐（ADR-0061 決定 5 の第 3 節）が"
            + "索引の側で評価できない");
    }

    // 🔴 受け入れ基準 6: **ON → 全 OFF は「属性で弾く」ではなく索引からの削除である。**
    // 索引を直接引いて 0 件を確かめる（`RecordingIndex` は削除を実際に反映する）。
    [Fact]
    public async Task 全てOFFへ戻すと索引から削除される()
    {
        var (consumer, index) = Build();

        await consumer.Handle(Event(PrivateNoteAttributes(search: true)),
            TestContext.Current.CancellationToken);
        index.Chunks.Should().ContainSingle("陽性対照: いったんは索引に載っている");

        await consumer.Handle(Event(PrivateNoteAttributes()),
            TestContext.Current.CancellationToken);

        index.Chunks.Should().BeEmpty(
            "ON → OFF は索引からの削除まで及ぶ（ADR-0061 決定 4）。"
            + "残った本文はフィルタの実装ミス 1 つで露出に変わる");
        index.Deleted.Should().Contain(NoteId);
    }

    // 決定 1 の「1 つでも」を軸ごとに固定する。**選言の 3 項すべてが効いていること**を測る ——
    // 1 項でも配線から落ちると、その用途だけ静かに索引されなくなる。
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task 露出のいずれか1つがONなら索引される(bool search, bool graph, bool ai)
    {
        var (consumer, index) = Build();

        await consumer.Handle(Event(PrivateNoteAttributes(search, graph, ai)),
            TestContext.Current.CancellationToken);

        index.Chunks.Should().ContainSingle();
    }

    // 露出属性が**欠落**した個人資料は fail-closed（索引しない）。
    // 陽性対照は上の「組織文書は索引される」——欠落の扱いが `doc_scope` で分かれることを対で示す。
    [Fact]
    public async Task 露出属性が欠落した個人資料は索引されない()
    {
        var (consumer, index) = Build();

        await consumer.Handle(
            Event(new Dictionary<string, string>
            {
                [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                ["owner"] = "alice",
            }),
            TestContext.Current.CancellationToken);

        index.Chunks.Should().BeEmpty("トグル属性の欠落は OFF 扱い（fail-closed）");
    }

    // 索引の**状態**を持つ器（呼び出しの記録ではない）。削除を実際に反映するので、
    // 「索引を直接引いて 0 件」（受け入れ基準 6）をそのまま測れる。
    private sealed class IndexedChunk(Guid documentId, Dictionary<string, string> attributes,
        List<string>? sharedWith)
    {
        public Guid DocumentId { get; } = documentId;
        public Dictionary<string, string> Attributes { get; } = attributes;
        public List<string>? SharedWith { get; } = sharedWith;
    }

    private sealed class RecordingIndex : IIngestionVectorStore
    {
        private readonly List<IndexedChunk> _chunks = [];

        public IReadOnlyList<IndexedChunk> Chunks => _chunks;
        public List<Guid> Deleted { get; } = [];

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
            string text, int chunkIndex, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default)
        {
            _chunks.Add(new IndexedChunk(documentId, attributes, sharedWith));
            return Task.CompletedTask;
        }

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
            string title, string indexText, float[] vector, string? markdownUri,
            Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default)
        {
            _chunks.Add(new IndexedChunk(documentId, attributes, sharedWith));
            return Task.CompletedTask;
        }

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        {
            Deleted.Add(documentId);
            _chunks.RemoveAll(c => c.DocumentId == documentId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubReader(string markdown) : IDocumentContentReader
    {
        public Task<string> ReadAsync(string uri, string title, CancellationToken ct = default)
            => Task.FromResult(markdown);
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
