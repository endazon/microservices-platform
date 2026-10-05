using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Features.Search.RemoveDeleted;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-03, FR-05, UC-01, ADR-0127 決定 1・2, ADR-0092 決定 1・3, [[IADR-0497]] 決定 5 (#1746):
// **語彙索引（ベクトルを持たないコレクション）を全文の系統だけで束ねる。**
//
// 🔴 測る守り:
//   (1) キーワード・ハイブリッドのモードでは語彙索引の文書が RRF に入って見つかる
//   (2) **意味検索のモードでは現れない**（語彙索引を問い合わせない）
//   (3) 語彙索引のクエリは**埋めない**（客体を呼ばない）。誤って埋められる客体を組んでもベクトル検索は引かない
//   (4) **ABAC は語彙索引の系統にも主と同じフィルタで掛かる**（実際に絞る索引で、権限外は出ない）
//   (5) 削除は語彙索引からも行う
[Trait("TestKind", "Unit")]
public class LexicalIndexFusionTests
{
    private const string Lexical = "knowledge_chunks_lexical";
    private static readonly AccessScope Granted = new([], true);

    private static SearchResultDto Hit(Guid id, float score, string confidentiality = "public") =>
        new(id, Guid.NewGuid(), "title", "text", score, "uri",
            new() { ["confidentiality"] = confidentiality }, [], null);

    private static FusedCollection LexicalCollection(IVectorStore store, IEmbeddingService? embed = null) =>
        new(Lexical, store, embed ?? NoQueryEmbedding.Instance, LexicalOnly: true);

    private static HybridSearchService Service(IVectorStore primary, IEmbeddingService primaryEmbed,
        ILogger<HybridSearchService>? logger = null, params FusedCollection[] fused) =>
        new(primary, primaryEmbed, logger ?? NullLogger<HybridSearchService>.Instance,
            new FusedCollections(fused));

    private static Task<List<SearchResultDto>> Search(
        HybridSearchService svc, string? mode, AccessScope? scope = null) =>
        svc.SearchAsync(new SearchRequest("問い", 10, null, scope ?? Granted, mode),
            TestSearchUser.Any, TestContext.Current.CancellationToken);

    // T-92 (ADR-0127 決定 2): キーワードのモード。語彙索引にだけ一致がある文書が見つかる。埋め込みは呼ばない。
    [Fact]
    public async Task キーワードでは語彙索引の文書が見つかる()
    {
        var secret = Guid.NewGuid();
        var primary = new ScriptedStore { Keyword = [Hit(Guid.NewGuid(), 1f)] };
        var lexical = new ScriptedStore { Keyword = [Hit(secret, 1f, "restricted")] };
        var embed = new FixedEmbedding([0.1f]);

        var results = await Search(Service(primary, embed, null, LexicalCollection(lexical)), SearchModes.Keyword);

        results.Should().Contain(r => r.ChunkId == secret);
        lexical.KeywordCalls.Should().Be(1);
        lexical.VectorCalls.Should().Be(0);
        embed.Calls.Should().Be(0);
    }

    // T-93 (ADR-0127 決定 2): ハイブリッドのモード。語彙索引の文書が全文の系統として RRF に入る。
    // 語彙索引のベクトル検索は引かず、**縮退の警告も出さない**（設計どおりの欠落を毎回鳴らさない）。
    [Fact]
    public async Task ハイブリッドでは語彙索引の文書が全文の系統として入り警告は出ない()
    {
        var secret = Guid.NewGuid();
        var open = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(open, 0.9f)], Keyword = [Hit(open, 1f)] };
        var lexical = new ScriptedStore { Keyword = [Hit(secret, 1f, "confidential")] };
        var logger = new WarningCountingLogger();

        var results = await Search(
            Service(primary, new FixedEmbedding([0.1f]), logger, LexicalCollection(lexical)), SearchModes.Hybrid);

        results.Select(r => r.ChunkId).Should().Equal(open, secret);
        results.Single(r => r.ChunkId == secret).Score.Should().Be((float)(1.0 / 61), "全文の系統 1 本の 1 位");
        lexical.VectorCalls.Should().Be(0);
        lexical.KeywordCalls.Should().Be(1);
        logger.Warnings.Should().Be(0, "語彙索引にベクトルの系統が無いのは縮退ではない");
    }

    // T-94 (ADR-0127 決定 2): **意味検索のモードでは語彙索引を問い合わせない**（現れない）。
    // 束ねたのが語彙索引だけなら、意味検索は従来の単一コレクションの経路のまま（生スコア・同じ呼び出し回数）。
    [Fact]
    public async Task 意味検索では語彙索引は現れず従来の経路のまま()
    {
        var open = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(open, 0.7f)] };
        var lexical = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.99f)], Keyword = [Hit(Guid.NewGuid(), 1f)] };
        var embed = new FixedEmbedding([0.1f]);

        var results = await Search(Service(primary, embed, null, LexicalCollection(lexical)), SearchModes.Semantic);

        results.Select(r => (r.ChunkId, r.Score)).Should().Equal((open, 0.7f));
        lexical.VectorCalls.Should().Be(0);
        lexical.KeywordCalls.Should().Be(0);
        embed.Calls.Should().Be(1);
    }

    // T-94（ベクトルの追加コレクションと並ぶ形）: 意味検索で束ねる経路に入っても、語彙索引は入らない。
    [Fact]
    public async Task ベクトルの追加コレクションと並んでも意味検索に語彙索引は入らない()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var primary = new ScriptedStore { Vector = [Hit(a, 0.9f)] };
        var ruri = new ScriptedStore { Vector = [Hit(b, 0.8f)] };
        var lexical = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.99f)] };

        var results = await Search(Service(primary, new FixedEmbedding([0.1f]), null,
            new FusedCollection("knowledge_chunks_ruri_v3", ruri, new FixedEmbedding([0.2f])),
            LexicalCollection(lexical)), SearchModes.Semantic);

        results.Select(r => r.ChunkId).Should().Equal(a, b);
        lexical.VectorCalls.Should().Be(0);
        lexical.KeywordCalls.Should().Be(0);
    }

    // T-95 (ADR-0127 決定 2・3 の前提): 語彙索引に**ベクトルを返す客体を誤って組んでも**、クエリは埋めず、
    // ベクトル検索も引かない（二重の守り。`LexicalOnly` の判定 1 つで落ちる）。
    [Theory]
    [InlineData(SearchModes.Hybrid)]
    [InlineData(SearchModes.Semantic)]
    [InlineData(SearchModes.Keyword)]
    public async Task 語彙索引はベクトルを返す客体でも埋めず引かない(string mode)
    {
        var lexical = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.99f)] };
        var misconfigured = new FixedEmbedding([0.3f]);

        await Search(Service(new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)] },
            new FixedEmbedding([0.1f]), null, LexicalCollection(lexical, misconfigured)), mode);

        misconfigured.Calls.Should().Be(0, "語彙索引のために検索語を埋め込みへ送らない");
        lexical.VectorCalls.Should().Be(0);
    }

    // T-95（意味検索で束ねる経路）: ベクトルの追加コレクションと並び、意味検索で束ねる経路に入っても、
    // 誤って組んだ客体は呼ばれず、語彙索引のベクトル検索も引かれない。
    // 🔴 二重の守り（埋めない・引かない）の**両方**を外すと落ちる（片方だけ外す変異は、もう片方が守るので生き残る ——
    // 作業仕様書 §変異試験の M10・M13）。
    [Fact]
    public async Task 意味検索で束ねる経路でも語彙索引はベクトルを返す客体でも埋めず引かない()
    {
        var lexical = new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.99f)] };
        var misconfigured = new FixedEmbedding([0.3f]);

        await Search(Service(new ScriptedStore { Vector = [Hit(Guid.NewGuid(), 0.5f)] }, new FixedEmbedding([0.1f]), null,
            new FusedCollection("knowledge_chunks_ruri_v3", new ScriptedStore(), new FixedEmbedding([0.2f])),
            LexicalCollection(lexical, misconfigured)), SearchModes.Semantic);

        misconfigured.Calls.Should().Be(0);
        lexical.VectorCalls.Should().Be(0);
    }

    // T-96 (ADR-0092 決定 3・ADR-0127 決定 2): **ABAC は語彙索引の系統にも主と同じフィルタで掛かる。**
    // 実際に絞る索引（`InMemoryVectorStore`）で、許さないスコープでは現れず、許すスコープでは現れる（陽性対照）。
    [Theory]
    [InlineData(SearchModes.Keyword)]
    [InlineData(SearchModes.Hybrid)]
    public async Task 語彙索引にもABACが掛かる(string mode)
    {
        var lexical = new InMemoryVectorStore();
        var secretDoc = Guid.NewGuid();
        await lexical.UpsertAsync(new ChunkPayload(Guid.NewGuid(), secretDoc, "人事評価", "問い の本文", [],
            "uri", new() { ["confidentiality"] = "restricted", ["department"] = "hr" }, []),
            TestContext.Current.CancellationToken);
        var primary = new ScriptedStore();

        var svc = Service(primary, new FixedEmbedding([]), null, LexicalCollection(lexical));
        var denied = await Search(svc, mode,
            new AccessScope([new AttributeFilter("confidentiality", ["public", "internal"])], true));
        var allowed = await Search(svc, mode,
            new AccessScope([new AttributeFilter("confidentiality", ["restricted"])], true));

        denied.Should().NotContain(r => r.DocumentId == secretDoc, "権限外の高機密文書は語彙索引からも出ない");
        allowed.Should().Contain(r => r.DocumentId == secretDoc, "陽性対照: 許すスコープなら見つかる");
    }

    // T-96（フィルタの同一性）: 語彙索引へ渡るフィルタは主の全文の系統と同じもの（省かない・緩めない）。
    [Fact]
    public async Task 語彙索引へ渡るフィルタは主と同じ()
    {
        var primary = new ScriptedStore();
        var lexical = new ScriptedStore();
        var scope = new AccessScope([new AttributeFilter("confidentiality", ["public"])], true);

        await Search(Service(primary, new FixedEmbedding([0.1f]), null, LexicalCollection(lexical)),
            SearchModes.Hybrid, scope);

        lexical.Filters.Should().ContainSingle().Which.Should().NotBeNull();
        lexical.Filters[0].Should().BeEquivalentTo(primary.Filters.Last(),
            "全文の系統へ渡るフィルタが主と同じである（ADR-0092 決定 3）");
        lexical.Filters[0]!.Conjunction.Should().ContainSingle(f => f.Key == "confidentiality");
    }

    // T-97 (FR-06, ADR-0057 決定 1, [[IADR-0497]] 決定 4): 文書削除のイベントで語彙索引からも消す。
    [Fact]
    public async Task 削除は語彙索引からも行う()
    {
        var primary = new ScriptedStore();
        var lexical = new ScriptedStore();
        var doc = Guid.NewGuid();

        await new DocumentDeletedConsumer(primary, new FusedCollections([LexicalCollection(lexical)]),
                ConsumerTimeoutsForTests.Calls(), DocumentDeletedTimeouts.Default,
                NullLogger<DocumentDeletedConsumer>.Instance)
            .Handle(new DocumentDeleted(doc, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        lexical.Deleted.Should().Equal(doc);
        primary.Deleted.Should().Equal(doc);
    }

    // T-98 ([[IADR-0497]] 決定 5): 語彙索引の名前は既定名へ倒れ、主・追加コレクションと同名なら起動を止める。
    [Fact]
    public void 語彙索引の名前は既定名へ倒れ_主や追加と同名なら止まる()
    {
        QdrantVectorStore.ResolveLexicalCollectionName(new ConfigurationBuilder().Build()).Should().Be(Lexical);
        QdrantVectorStore.ResolveLexicalCollectionName(new ConfigurationBuilder()
            .AddInMemoryCollection([new("Qdrant:LexicalCollection", " other ")]).Build()).Should().Be("other");

        var primarySame = () => QdrantVectorStore.EnsureLexicalCollectionDistinct("v", "v", []);
        var fusedSame = () => QdrantVectorStore.EnsureLexicalCollectionDistinct("r", "v", ["r"]);
        primarySame.Should().Throw<InvalidOperationException>().WithMessage("*Qdrant:LexicalCollection*");
        fusedSame.Should().Throw<InvalidOperationException>();
        QdrantVectorStore.EnsureLexicalCollectionDistinct(Lexical, "v", ["r"]);   // 陽性対照
    }

    // T-98: クエリを埋めない客体は、常に空ベクトルを返す（ゲートウェイを呼ばない）。
    [Fact]
    public async Task クエリを埋めない客体は空を返す()
    {
        (await NoQueryEmbedding.Instance.EmbedAsync("問い", TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    private sealed class WarningCountingLogger : ILogger<HybridSearchService>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings++;
        }
    }
}
