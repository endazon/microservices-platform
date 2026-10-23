using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02, FR-05, UC-04, [[IADR-0595]] (#2101): **取り込みの順序と原子性。**
//
// 索引の状態を持つ偽物（`StatefulIndex`。版の印・コレクションごとの点・旧版の削除の条件を実装と同じ意味で持つ）の上で、
// 次の場面を固定する:
//   - 到着順の逆転（新しい版の後に古い版が届く）—— 古い版は反映されず、判定軸を厳しくした版が緩まない
//   - 再配送（同じ版が 2 度届く）—— 点は増えず、同じ内容に収束する
//   - 撤収・削除の後に古い版が届く —— 点は書き戻されない
//   - 本文の取得・書き込みの失敗 —— 判定軸が同じなら旧版が残る（文書が消えない）。変わったなら旧版は残らない（fail-closed）
//   - 新版を書き終えた後の後始末 —— チャンク数の減少・置き場所の移動で旧版の点が残らない
//   - 取り込みの最中に新しい版が反映された —— 追い越された版の点は残らない
//
// 🔴 **陰性（載らない）の主張には陽性対照を対で置く**（索引が空だっただけの緑にしない）。
[Trait("TestKind", "Unit")]
public class IngestionOrderingAndAtomicityTests
{
    private static readonly Guid DocId = Guid.Parse("21010000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset V1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset V2 = V1.AddMinutes(5);

    private const string TwoChunks = "# 見出しA\n\n本文アルファ\n\n# 見出しB\n\n本文ベータ";
    private const string ThreeChunks = "# A\n\nいち\n\n# B\n\nに\n\n# C\n\nさん";
    private const string OneChunk = "# A\n\n改訂後の本文";

    private static DocumentUpdated Event(DateTimeOffset updatedAt, string confidentiality = "internal",
        string status = "published", string? markdownUri = "storage://b/doc.md", List<string>? sharedWith = null)
        => new(DocId, "規程", status, markdownUri,
            new Dictionary<string, string> { ["confidentiality"] = confidentiality }, ["ops"], updatedAt,
            SharedWith: sharedWith);

    private static DocumentUpdatedConsumer Build(StatefulIndex index, ScriptedReader reader, ScriptedEmbedder? embed = null)
        => new(
            reader,
            new MarkdownChunkingService(),
            embed ?? new ScriptedEmbedder(),
            index,
            new NoopCompleted(),
            ConsumerTimeoutsForTests.Calls(),
            ConsumerTimeoutsForTests.Timeouts,
            TimeProvider.System,
            TestMeterFactory.NewEmbeddingPendingMetrics(),
            TestMeterFactory.NewBodyMissingMetrics(),
            NullLogger<DocumentUpdatedConsumer>.Instance);

    private static Task Handle(DocumentUpdatedConsumer consumer, DocumentUpdated ev)
        => consumer.Handle(ev, TestContext.Current.CancellationToken);

    // ── 到着順の逆転 ──

    // 🔴 GraphService の「保持中より古いイベントは適用されない_厳格化後に緩和が復活しない」を取り込みへ写す。
    // 新しい版（confidential へ厳格化）の後に古い版（internal）が届いても、索引は新しい版のまま残る。
    [Fact]
    public async Task 保持中より古い更新は反映されない_厳格化後に緩和が復活しない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V2, "confidential"));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照: 新しい版は索引される");

        await Handle(consumer, Event(V1, "internal"));

        index.PointsOf(DocId).Should().OnlyContain(p => p.Confidentiality == "confidential",
            "古い版（internal）が新しい版（confidential）を上書きしてはならない");
        index.PointsOf(DocId).Should().OnlyContain(p => p.UpdatedAtMs == V2.ToUnixTimeMilliseconds());
        index.PointsOf(DocId).Should().OnlyContain(p => p.Collection == StatefulIndex.LexicalName,
            "confidential は語彙索引だけに載る（古い版の埋め込みのコレクションの点が復活しない）");
        reader.Reads.Should().Be(1, "古い版は本文を読む前に弾かれる");
        index.Versions[DocId].UpdatedAtMs.Should().Be(V2.ToUnixTimeMilliseconds(), "版の印は新しい版のまま");
    }

    // 共有先を外した版の後に、外す前の版が届いても、共有先は復活しない（判定軸は機密区分だけではない）。
    [Fact]
    public async Task 共有先を外した版の後に古い版が届いても共有先は復活しない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V2, sharedWith: []));
        await Handle(consumer, Event(V1, sharedWith: ["user-x"]));

        index.PointsOf(DocId).Should().NotBeEmpty();
        index.PointsOf(DocId).Should().OnlyContain(p => p.SharedWith.Count == 0, "外した共有先が古い版で戻らない");
    }

    // ── 再配送 ──

    // 同じ版が 2 度届いても点は増えず、同じ内容に収束する（等しい版は反映する —— 再配送・再発行は同じ版を運ぶ）。
    [Fact]
    public async Task 同じ版の再配送は点を増やさず同じ内容に収束する()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        var first = index.PointsOf(DocId).Select(p => (p.Collection, p.Id, p.Text)).ToList();

        await Handle(consumer, Event(V1));

        reader.Reads.Should().Be(2, "等しい版は弾かない（再配送・再発行を反映する）");
        index.PointsOf(DocId).Select(p => (p.Collection, p.Id, p.Text)).Should().BeEquivalentTo(first);
        first.Should().HaveCount(2);
    }

    // ── 撤収・削除の後に古い版が届く ──

    // 🔴 アーカイブ（撤収）した後に、撤収前の古い版が届いても点は書き戻されない。陽性対照: 撤収前は索引されていた。
    [Fact]
    public async Task 撤収した後に古い版が届いても点は書き戻されない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照: 撤収前は索引されている");

        await Handle(consumer, Event(V2, status: DocumentArchival.ArchivedStatus));
        index.PointsOf(DocId).Should().BeEmpty();

        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().BeEmpty("撤収した版より古い版は反映しない");
    }

    // 🔴 文書の削除（検索側が点を全部消す）の後に、削除前より古い版が遅れて届いても点は書き戻されない
    // （版の印は点と別の場所にあり、点を消しても残る）。
    [Fact]
    public async Task 削除で点が消えた後に古い版が届いても点は書き戻されない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V2));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照");

        index.RemoveAllPointsOf(DocId); // 検索側（RetrievalService）の削除の受け口が点を消した状態

        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().BeEmpty("削除前の版より古い版は反映しない");
    }

    // ── 失敗しても消えない／緩まない ──

    // 🔴 判定軸が同じ更新で本文の取得が失敗しても（再試行を使い切りデッドレターへ落ちても）、旧版の点は残る。
    [Fact]
    public async Task 判定軸が同じ更新で本文の取得が失敗しても旧版の点は残る()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        var before = index.PointsOf(DocId).Select(p => p.Text).ToList();
        before.Should().HaveCount(2);

        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V2));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Select(p => p.Text).Should().BeEquivalentTo(before, "文書は索引から消えない");
    }

    // 🔴 判定軸が同じ更新で書き込みが途中で失敗しても、旧版の点は残る（書き換わったチャンクは新版、残りは旧版）。
    [Fact]
    public async Task 判定軸が同じ更新で書き込みが途中で失敗しても文書は索引から消えない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));

        reader.Body = ThreeChunks;
        index.FailAfter(1); // ここから 2 点目の書き込みで失敗する
        var act = () => Handle(consumer, Event(V2));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().HaveCount(2, "旧版の点を先に消していないので、文書は索引に残る");
    }

    // 🔴 判定軸が変わる更新（internal → confidential）で本文の取得が失敗したら、旧版（緩い判定軸）の点は残らない（fail-closed）。
    // 陽性対照: 判定軸が同じ更新の失敗では旧版が残る（上の試験）。
    [Fact]
    public async Task 判定軸が変わる更新で失敗したら旧版の緩い点は残らない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1, "internal"));
        index.PointsOf(DocId).Should().NotBeEmpty();

        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V2, "confidential"));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().BeEmpty("判定軸を変える更新は旧版を先に消す（失敗しても緩い判定軸の点を残さない）");

        // 再試行（同じ版の再配送）は成功すれば新しい判定軸で載る。
        reader.FailNext = false;
        await Handle(consumer, Event(V2, "confidential"));
        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.Confidentiality == "confidential");
    }

    // 印の無い文書（移行前に索引された文書）は判定軸が「不明」なので、先に消す（従前の挙動・fail-closed）。
    [Fact]
    public async Task 版の印が無い文書は旧版を先に消す()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        index.Seed("voyage", ChunkId.Derive(DocId, 7), DocId, updatedAtMs: null, "internal"); // 移行前の点（版を持たない）
        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V1));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().BeEmpty();
    }

    // ── 書き終えた後の後始末 ──

    // チャンク数が減った版（3 → 1）を書き終えたら、旧版の余ったチャンクは消える（先に全消ししない形でも残らない）。
    [Fact]
    public async Task チャンク数が減った版を書き終えると旧版の余りの点は消える()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(ThreeChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().HaveCount(3);

        reader.Body = OneChunk;
        await Handle(consumer, Event(V2));

        index.PointsOf(DocId).Should().ContainSingle().Which.Text.Should().Contain("改訂後の本文");
    }

    // 埋め込み待ち（語彙索引）だった版の後に埋め込める版が来たら、語彙索引の待ちの点は消える（置き場所の移動）。
    // 同じ版の再発行（版が等しい）でも残らない —— 後始末は「この取り込みで書いていない点」を消す。
    [Fact]
    public async Task 置き場所が移った同じ版の再発行でも元の置き場所の点は残らない()
    {
        var index = new StatefulIndex();
        var embed = new ScriptedEmbedder { Embedded = false };
        var consumer = Build(index, new ScriptedReader(TwoChunks), embed);

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().OnlyContain(p => p.Collection == StatefulIndex.LexicalName && p.Pending);

        embed.Embedded = true;
        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().HaveCount(2).And.OnlyContain(p => p.Collection == "voyage" && !p.Pending);
    }

    // ── 追い越し ──

    // 🔴 古い版の取り込みの最中に新しい版が反映されたら、古い版の点は残らない（新しい版の点だけが残る）。
    // 本文の取得の中で新しい版（confidential）を丸ごと取り込み、古い版（internal）はその後に書き込む —— 印の読み直しで追い越しを知る。
    [Fact]
    public async Task 取り込みの最中に新しい版が反映されたら古い版の点は残らない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);
        var nested = Build(index, new ScriptedReader(OneChunk));
        reader.OnRead = () => Handle(nested, Event(V2, "confidential"));

        await Handle(consumer, Event(V1, "internal"));

        index.PointsOf(DocId).Should().ContainSingle()
            .Which.Should().Match<StatefulIndex.Point>(p => p.Confidentiality == "confidential"
                                                            && p.UpdatedAtMs == V2.ToUnixTimeMilliseconds());
    }

    // ── 純関数 ──

    [Fact]
    public void 判定はミリ秒で比べ_等しい版と印の無い文書は反映する()
    {
        var indexed = new DocumentIndexVersion(V2, "x");
        DocumentIndexOrdering.IsStale(indexed, V1).Should().BeTrue();
        DocumentIndexOrdering.IsStale(indexed, V2).Should().BeFalse();
        DocumentIndexOrdering.IsStale(indexed, V2.AddTicks(9_000)).Should().BeFalse("ミリ秒未満の差は等しい版として読む");
        DocumentIndexOrdering.IsStale(indexed, V2.AddMilliseconds(-1)).Should().BeTrue();
        DocumentIndexOrdering.IsStale(null, V1).Should().BeFalse();
    }

    [Fact]
    public void 判定軸の指紋は鍵と共有先の順に依らず_値が変われば変わる()
    {
        var a = IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" }, ["u", "v"]);
        var b = IndexAccessFingerprint.Of(new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" }, ["v", "u", "u"]);
        a.Should().Be(b);
        IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "3" }, ["u", "v"]).Should().NotBe(a);
        IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" }, ["u"]).Should().NotBe(a);
        IndexAccessFingerprint.Of(new Dictionary<string, string>(), null)
            .Should().Be(IndexAccessFingerprint.Of(new Dictionary<string, string>(), []));
    }

    // ── 部品 ──

    // 索引の状態を持つ偽物。点の削除・版の印・旧版の削除の条件は実装（`QdrantIngestionVectorStore`）と同じ意味で持つ。
    internal sealed class StatefulIndex : IIngestionVectorStore
    {
        public const string LexicalName = "lexical";

        public sealed record Point(string Collection, Guid Id, Guid DocumentId, long? UpdatedAtMs, string Confidentiality,
            IReadOnlyList<string> SharedWith, string Text, bool Pending);

        private readonly Dictionary<(string Collection, Guid Id), Point> _points = [];
        public Dictionary<Guid, DocumentIndexVersion> Versions { get; } = [];
        private int? _failAfter;
        private int _upserts;

        // ここから数えて n 点を書いた後の書き込みを失敗させる。
        public void FailAfter(int n)
        {
            _failAfter = n;
            _upserts = 0;
        }

        public IReadOnlyList<Point> PointsOf(Guid documentId) => [.. _points.Values.Where(p => p.DocumentId == documentId)];

        public void RemoveAllPointsOf(Guid documentId)
        {
            foreach (var key in _points.Where(p => p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
                _points.Remove(key);
        }

        public void Seed(string collection, Guid id, Guid documentId, long? updatedAtMs, string confidentiality)
            => _points[(collection, id)] = new Point(collection, id, documentId, updatedAtMs, confidentiality, [], "seed", false);

        private Task Write(string collection, Guid id, Guid documentId, DateTimeOffset? updatedAt,
            Dictionary<string, string> attributes, List<string>? sharedWith, string text, bool pending)
        {
            if (_failAfter is { } n && _upserts >= n)
                throw new IOException("qdrant write failed");
            _upserts++;
            _points[(collection, id)] = new Point(collection, id, documentId, updatedAt?.ToUnixTimeMilliseconds(),
                attributes.GetValueOrDefault("confidentiality") ?? "", sharedWith ?? [], text, pending);
            return Task.CompletedTask;
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, float[] vector, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Write(collection, chunkId, documentId, updatedAt, attributes, sharedWith, text, false);

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default)
            => Write(collection, pointId, documentId, updatedAt, attributes, sharedWith, indexText, false);

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text, int chunkIndex,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Write(LexicalName, chunkId, documentId, updatedAt, attributes, sharedWith, text, false);

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title, string indexText,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, CancellationToken ct = default)
            => Write(LexicalName, pointId, documentId, updatedAt, attributes, sharedWith, indexText, false);

        public Task UpsertEmbeddingPendingChunkAsync(Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, CancellationToken ct = default)
            => Write(LexicalName, chunkId, documentId, updatedAt, attributes, sharedWith, text, true);

        public Task UpsertEmbeddingPendingMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, CancellationToken ct = default)
            => Write(LexicalName, pointId, documentId, updatedAt, attributes, sharedWith, indexText, true);

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        {
            RemoveAllPointsOf(documentId);
            return Task.CompletedTask;
        }

        public Task<DocumentIndexVersion?> GetIndexVersionAsync(Guid documentId, CancellationToken ct = default)
            => Task.FromResult(Versions.TryGetValue(documentId, out var v) ? v : null);

        public Task SetIndexVersionAsync(Guid documentId, DocumentIndexVersion version, CancellationToken ct = default)
        {
            Versions[documentId] = version with { UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(version.UpdatedAtMs) };
            return Task.CompletedTask;
        }

        // 実装のフィルタと同じ条件: 文書の点 ∧ ¬(updated_at > version) ∧ ¬(この取り込みで書いた点)。
        public Task DeleteSupersededAsync(Guid documentId, DateTimeOffset version,
            IReadOnlyCollection<IndexedPointRef> keep, CancellationToken ct = default)
        {
            var ms = version.ToUnixTimeMilliseconds();
            var kept = keep.Select(k => (k.Collection ?? LexicalName, k.PointId)).ToHashSet();
            foreach (var key in _points
                         .Where(p => p.Value.DocumentId == documentId
                                     && !(p.Value.UpdatedAtMs is { } at && at > ms)
                                     && !kept.Contains(p.Key))
                         .Select(p => p.Key).ToList())
                _points.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedReader(string body) : IDocumentContentReader
    {
        public string Body { get; set; } = body;
        public bool FailNext { get; set; }
        public Func<Task>? OnRead { get; set; }
        public int Reads { get; private set; }

        public async Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        {
            Reads++;
            if (FailNext)
                throw new IOException("storage unavailable");
            if (OnRead is { } hook)
            {
                OnRead = null;
                await hook();
            }

            return Body;
        }
    }

    // public / internal は埋め込みのコレクション `voyage` へ（`Embedded=false` なら一時障害として埋め込み待ちへ）。
    private sealed class ScriptedEmbedder : IEmbeddingService
    {
        public bool Embedded { get; set; } = true;

        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
            => Task.FromResult(Embedded
                ? new EmbeddingResult([0.1f, 0.2f], "voyage", true)
                : new EmbeddingResult([], "", Embedded: false, Retryable: true));
    }

    private sealed class NoopCompleted : IIngestionCompletedPublisher
    {
        public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
