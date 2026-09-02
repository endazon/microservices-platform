using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02, UC-04: IngestionService 取り込みパイプライン（parse→chunk→embed→index）テスト
//
// E3b: 購読は Wolverine 段になった。本ファイルは Handle を直接呼ぶ（測るのは取り込みの写像で
// あって配送ではない。E1 の器の選び分けと同じ —— 登録経路・実配送は別の器が持つ）。
// IngestionCompleted の発行は IIngestionCompletedPublisher（ポート）への引数で観測する。
public class DocumentUpdatedConsumerTests
{
    private static DocumentUpdated SampleEvent(
        string? markdownUri = "https://storage.example/docs/test.md",
        string confidentiality = "internal",
        DateTimeOffset? updatedAt = null)
        => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "テスト文書",
            "normalized",
            markdownUri,
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            ["knowledge-mgmt", "ops"],
            updatedAt ?? DateTimeOffset.UtcNow);

    private static (DocumentUpdatedConsumer Consumer, RecordingCompletedPublisher Completed) Build(
        IIngestionVectorStore store,
        IDocumentContentReader reader,
        IEmbeddingService? embed = null)
    {
        var completed = new RecordingCompletedPublisher();
        var consumer = new DocumentUpdatedConsumer(
            reader,
            new MarkdownChunkingService(),
            embed ?? new StubEmbeddingService(),
            store,
            completed,
            NullLogger<DocumentUpdatedConsumer>.Instance);
        return (consumer, completed);
    }

    private static Task HandleAsync(DocumentUpdatedConsumer consumer, DocumentUpdated ev)
        => consumer.Handle(ev, TestContext.Current.CancellationToken);

    // T-01 / T-02: パイプラインが消費し、パース本文由来のチャンクが索引登録される
    [Fact]
    public async Task Consumer_ShouldParseChunkEmbedAndIndex()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# 見出しA\n\n本文アルファ\n\n# 見出しB\n\n本文ベータ");
        var (consumer, _) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent());

        // 見出し 2 つ → 2 チャンク。パース本文の内容がチャンクに反映される。
        store.Upserts.Should().HaveCount(2);
        store.Upserts.Select(u => u.Text).Should().Contain(t => t.Contains("本文アルファ"));
    }

    // FR-03, SC-02, #536（IADR-0149 決定 5）: **更新日時はイベントが運んできた値をそのまま索引へ渡す。**
    // 取り込み側で `DateTimeOffset.UtcNow` を採ると、**再索引のたびに全文書の「更新日時」が今になり**、
    // 計画が並び順を求めた動機（「規程が改訂されたはずだが最新版はどれか」）が成立しなくなる。
    // 過去の日時を渡して「今」に化けていないことを見る（現在時刻を渡すと両者が区別できない）。
    [Fact]
    public async Task Consumer_ShouldIndexDocumentUpdatedAt_NotIngestionTime()
    {
        var documentUpdatedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(9));
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");
        var (consumer, _) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent(updatedAt: documentUpdatedAt));

        store.Upserts.Should().NotBeEmpty();
        store.Upserts.Should().OnlyContain(u => u.UpdatedAt == documentUpdatedAt,
            "索引には文書の更新日時が載る（取り込み時刻ではない）");
    }

    // T-03: ペイロードに chunk_index / tags / attributes が保持される
    [Fact]
    public async Task Consumer_ShouldPreserveChunkIndexTagsAndAttributes()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");
        var (consumer, _) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent());

        store.Upserts.Select(u => u.ChunkIndex).Should().BeEquivalentTo(new[] { 0, 1 });
        store.Upserts.Should().OnlyContain(u => u.Tags.Contains("knowledge-mgmt"));
        store.Upserts.Should().OnlyContain(u => u.Attributes["confidentiality"] == "internal");
    }

    // T-04: 決定的チャンク ID により再取り込みで ID が一致する（冪等）
    [Fact]
    public async Task Consumer_ShouldUseDeterministicChunkIds_AcrossReingestion()
    {
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");

        async Task<List<Guid>> IngestOnce()
        {
            var store = new RecordingVectorStore();
            var (consumer, _) = Build(store, reader);
            await HandleAsync(consumer, SampleEvent());
            return store.Upserts.Select(u => u.ChunkId).ToList();
        }

        var first = await IngestOnce();
        var second = await IngestOnce();
        first.Should().Equal(second);
    }

    // T-06: 取り込み完了で IngestionCompleted が発行される（ポートの引数で観測する）
    [Fact]
    public async Task Consumer_ShouldPublishIngestionCompleted()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる");
        var (consumer, completed) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent());

        completed.Published.Should().ContainSingle();
        completed.Published[0].DocumentId.Should().Be(SampleEvent().DocumentId);
        completed.Published[0].ChunkCount.Should().Be(1);
    }

    // T-05: MarkdownUri が null なら登録 0 件で正常終了する（例外フロー E1）
    [Fact]
    public async Task Consumer_ShouldSkip_WhenMarkdownUriIsNull()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("ignored");
        var (consumer, completed) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent(markdownUri: null));

        store.Upserts.Should().BeEmpty();
        completed.Published.Should().BeEmpty();
    }

    // T-07 (FR-02, ADR-0016): 文書の機密区分（confidentiality）が埋め込みへ渡される。
    // ゲートウェイが越境判定（fail-closed）とモデル別コレクションを決めるために必須。
    [Fact]
    public async Task Consumer_ShouldPassConfidentialityToEmbedding()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる");
        var embed = new RecordingEmbeddingService(); // 既定: voyage コレクション・Embedded=true
        var (consumer, _) = Build(store, reader, embed);

        await HandleAsync(consumer, SampleEvent()); // confidentiality=internal

        embed.Requests.Should().OnlyContain(r => r.Confidentiality == "internal");
    }

    // T-08 (FR-02, ADR-0016): ゲートウェイが返したモデル別コレクションへ索引する。
    [Fact]
    public async Task Consumer_ShouldIndexToRoutedCollection()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");
        var embed = new RecordingEmbeddingService(collection: "knowledge_chunks_voyage_3_5");
        var (consumer, _) = Build(store, reader, embed);

        await HandleAsync(consumer, SampleEvent());

        store.Upserts.Should().OnlyContain(u => u.Collection == "knowledge_chunks_voyage_3_5");
    }

    // T-09 (FR-02, FR-05, ADR-0016): fail-closed。埋め込みが Embedded=false（高機密でセルフホスト未有効等）なら
    // 索引しない（外部へ本文を送らず、Qdrant へも書かない）。
    [Fact]
    public async Task Consumer_ShouldSkipIndexing_WhenEmbeddingFailClosed()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる\n\n# B\n\nばつ");
        var embed = new RecordingEmbeddingService(embedded: false); // fail-closed
        var (consumer, _) = Build(store, reader, embed);

        await HandleAsync(consumer, SampleEvent(confidentiality: "confidential"));

        // 索引は 0 件（fail-closed で書き込まない）。埋め込みは試行される（機密区分は渡る）。
        store.Upserts.Should().BeEmpty();
        embed.Requests.Should().OnlyContain(r => r.Confidentiality == "confidential");
    }

    // T-11 (FR-02, Issue #98): 一時的な埋め込み障害（Retryable=true）は fail-closed（意図的スキップ）と区別し、
    // 例外を送出して再試行/DLQ に委ねる。恒久スキップ（IngestionCompleted の縮小 chunkCount 発行）にしない。
    [Fact]
    public async Task Consumer_ShouldFaultForRetry_WhenEmbeddingTransientFailure()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる");
        // 一時障害（送信先不調等）を模す: Embedded=false かつ Retryable=true。
        var embed = new RecordingEmbeddingService(embedded: false, retryable: true);
        var (consumer, completed) = Build(store, reader, embed);

        // 例外を送出して失敗する（ブローカの再試行/DLQ へ委ねる。恒久スキップにはしない）。
        var act = () => HandleAsync(consumer, SampleEvent());
        await act.Should().ThrowAsync<EmbeddingTransientException>();

        // 一時障害では索引せず、完了イベントも発行しない（取りこぼしを DLQ で検知可能にする）。
        store.Upserts.Should().BeEmpty();
        completed.Published.Should().BeEmpty();
    }

    // T-10 (FR-02, FR-05, ADR-0016): 取り込み冒頭で全モデル別コレクションから当該文書を削除する
    // （機密区分変更時の旧コレクション残存＝ABAC バイパスを防止）。
    [Fact]
    public async Task Consumer_ShouldDeleteFromAllCollections_BeforeIndexing()
    {
        var store = new RecordingVectorStore();
        var reader = new StubContentReader("# A\n\nまる");
        var (consumer, _) = Build(store, reader);

        await HandleAsync(consumer, SampleEvent());

        store.DeletedFromAll.Should().Contain(SampleEvent().DocumentId);
    }
}

// テスト用スタブ群
file class StubEmbeddingService : IEmbeddingService
{
    // 既定は voyage コレクション（1024次元）・索引可（Embedded=true）を模す。
    public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
        => Task.FromResult(new EmbeddingResult(new float[1024], "knowledge_chunks_voyage_3_5", true));
}

// 呼び出しを記録し、コレクション/成否/一時障害を制御できるスタブ。
file class RecordingEmbeddingService(
    string collection = "knowledge_chunks_voyage_3_5",
    bool embedded = true,
    int dimensions = 1024,
    bool retryable = false) : IEmbeddingService
{
    public List<(string Text, string? Confidentiality)> Requests { get; } = [];

    public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
    {
        Requests.Add((text, confidentiality));
        return Task.FromResult(new EmbeddingResult(
            embedded ? new float[dimensions] : [], collection, embedded, retryable));
    }
}

file class StubContentReader(string markdown) : IDocumentContentReader
{
    public Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult(markdown);
}

// E3b: IngestionCompleted の発行口（ポート）の記録ダブル。
class RecordingCompletedPublisher : IIngestionCompletedPublisher
{
    public List<(Guid DocumentId, int ChunkCount, DateTimeOffset CompletedAt)> Published { get; } = [];

    public Task PublishCompletedAsync(
        Guid documentId, int chunkCount, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        Published.Add((documentId, chunkCount, completedAt));
        return Task.CompletedTask;
    }
}

file record UpsertRecord(string Collection, Guid ChunkId, Guid DocumentId, string Title, string Text,
    int ChunkIndex, string? MarkdownUri, Dictionary<string, string> Attributes, List<string> Tags,
    DateTimeOffset? UpdatedAt);

file class RecordingVectorStore : IIngestionVectorStore
{
    public List<UpsertRecord> Upserts { get; } = [];
    public List<Guid> DeletedFromAll { get; } = [];
    public bool CollectionsEnsured { get; private set; }

    public Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        CollectionsEnsured = true;
        return Task.CompletedTask;
    }

    public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null, CancellationToken ct = default)
    {
        Upserts.Add(new UpsertRecord(collection, chunkId, documentId, title, text, chunkIndex,
            markdownUri, attributes, tags, updatedAt));
        return Task.CompletedTask;
    }

    public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
    {
        DeletedFromAll.Add(documentId);
        return Task.CompletedTask;
    }
}
