using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Common.Observability;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-03, FR-04, NFR-06, ADR-0016, ADR-0018, ADR-0035, ADR-0127 決定 2・3, [[IADR-0534]] (#1871):
// **検索の縮退の印**（`HybridSearchResult.DegradedReasons`）をサービス層で固定する。
//
// 受け入れ基準 A1（機械可読の印）・A2（固定語彙）・A4（計数を揃える）の写像。
// 陽性（縮退する）と陰性（設計どおりで縮退ではない）を対で置く —— 印が常に立つ・常に立たない実装を両方落とすため。
public class SearchDegradationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SearchUserContext User = TestSearchUser.Any;

    private static SearchRequest Request(string? mode = null, bool granted = true) =>
        new("アルファ", 10, Scope: new AccessScope([], GrantsAccess: granted), Mode: mode);

    private static SearchResultDto Hit(string name) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, name, 1f, null, new() { ["confidentiality"] = "public" }, []);

    private static RecordingVectorStore Store() =>
        new() { VectorResults = [Hit("v")], KeywordResults = [Hit("k")] };

    private static HybridSearchService Service(
        IVectorStore store, IEmbeddingService embed, FusedCollections? fused = null,
        ISearchReranker? reranker = null, SearchDegradationMetrics? metrics = null) =>
        new(store, embed, NullLogger<HybridSearchService>.Instance, fused, reranker, metrics);

    // ── A2: 固定語彙 ─────────────────────────────────────────

    // 🔴 値域は 4 つに閉じる（本文・URL・例外メッセージが入る余地を持たない）。足すなら proto の enum と同時に足す。
    [Fact]
    public void 理由の語彙は4つの固定値に閉じている()
    {
        SearchDegradedReasons.All.Should().Equal(
            "embed-failed", "fused-embed-failed", "graph-expand-failed", "rerank-failed");
    }

    [Fact]
    public void 理由は正の順に並び重複しない()
    {
        SearchDegradedReasons.Normalize([
                SearchDegradedReasons.RerankFailed, SearchDegradedReasons.EmbedFailed,
                SearchDegradedReasons.RerankFailed])
            .Should().Equal(SearchDegradedReasons.EmbedFailed, SearchDegradedReasons.RerankFailed);
    }

    [Fact]
    public void 応答の印は理由と常に一致する()
    {
        var degraded = new SearchResponse([], 0, 0).WithDegradation([SearchDegradedReasons.EmbedFailed]);
        degraded.Degraded.Should().BeTrue();
        degraded.DegradedReasons.Should().Equal(SearchDegradedReasons.EmbedFailed);

        var healthy = new SearchResponse([], 0, 0).WithDegradation([]);
        healthy.Degraded.Should().BeFalse();
        healthy.DegradedReasons.Should().BeEmpty();

        new SearchResponse([], 0, 0).Degraded.Should().BeFalse("早期の空応答は既定で縮退なし");
    }

    // ── A1: 埋め込みの縮退 ──────────────────────────────────

    // ★ PoC の事象そのもの: 埋め込みが空ベクトルで返ると、hybrid は語彙検索だけで返る。印は embed-failed。
    [Fact]
    public async Task hybridで埋め込みが空なら語彙検索の結果にembed_failedが添えられる()
    {
        var result = await Service(Store(), new EmptyVectorEmbeddingService())
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Results.Should().ContainSingle().Which.DocumentTitle.Should().Be("k", "語彙検索だけで続行する（#995）");
        result.Degraded.Should().BeTrue();
        result.DegradedReasons.Should().Equal(SearchDegradedReasons.EmbedFailed);
    }

    // 陰性対照: 埋め込みが得られれば印は立たない（常に立つ実装を落とす）。
    [Fact]
    public async Task 埋め込みが得られれば縮退の印は立たない()
    {
        var result = await Service(Store(), new CountingEmbeddingService())
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Results.Should().HaveCount(2);
        result.Degraded.Should().BeFalse();
        result.DegradedReasons.Should().BeEmpty();
    }

    // semantic は全文へ振り替えない（#995）ので 0 件になる。0 件の理由が「該当なし」ではないことを印で区別できる。
    [Fact]
    public async Task semanticで埋め込みが空なら0件にembed_failedが添えられる()
    {
        var result = await Service(Store(), new EmptyVectorEmbeddingService())
            .SearchWithDegradationAsync(Request(SearchModes.Semantic), User, Ct);

        result.Results.Should().BeEmpty();
        result.DegradedReasons.Should().Equal(SearchDegradedReasons.EmbedFailed);
    }

    // keyword モードは埋め込みを呼ばない —— 縮退ではない。
    [Fact]
    public async Task keywordモードは埋め込みを呼ばないので縮退ではない()
    {
        var embed = new EmptyVectorEmbeddingService();
        var result = await Service(Store(), embed)
            .SearchWithDegradationAsync(Request(SearchModes.Keyword), User, Ct);

        embed.Calls.Should().Be(0);
        result.Degraded.Should().BeFalse();
    }

    // 🔴 存在秘匿（[[IADR-0009]] / [[IADR-0313]] 決定 1）: deny は埋め込みより前に空で返る。印は立たない ——
    // 「権限が無い」と「該当が無い」を印で区別させない（埋め込みが壊れていても deny は同じ形で返る）。
    [Fact]
    public async Task denyは埋め込みが壊れていても縮退の印を持たない()
    {
        var embed = new EmptyVectorEmbeddingService();
        var result = await Service(Store(), embed)
            .SearchWithDegradationAsync(Request(granted: false), User, Ct);

        embed.Calls.Should().Be(0, "deny は部品を呼ばない");
        result.Results.Should().BeEmpty();
        result.Degraded.Should().BeFalse();
    }

    // 追加コレクション（ベクトルの系統あり）の埋め込みが空なら fused-embed-failed。主は健全なので embed-failed は立たない。
    [Fact]
    public async Task 追加コレクションの埋め込みが空ならfused_embed_failedが添えられる()
    {
        var fused = new FusedCollections([
            new FusedCollection("tier-a", Store(), new EmptyVectorEmbeddingService()),
        ]);

        var result = await Service(Store(), new CountingEmbeddingService(), fused)
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.DegradedReasons.Should().Equal(SearchDegradedReasons.FusedEmbedFailed);
    }

    // 陰性対照（ADR-0127 決定 2）: 語彙索引は設計どおりベクトルを持たない —— 縮退に数えない
    // （数えると既定構成〔語彙索引を常に束ねる〕の全検索が縮退になり、印が意味を失う）。
    [Fact]
    public async Task 語彙索引にベクトルが無いことは縮退ではない()
    {
        var fused = new FusedCollections([
            new FusedCollection("lexical", Store(), NoQueryEmbedding.Instance, LexicalOnly: true),
        ]);

        var result = await Service(Store(), new CountingEmbeddingService(), fused)
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Degraded.Should().BeFalse();
    }

    // semantic を束ねる経路でも、主と追加の両方の埋め込みの縮退が正の順で並ぶ。
    [Fact]
    public async Task 束ねたsemanticで全部の埋め込みが空なら両方の理由が添えられる()
    {
        var fused = new FusedCollections([
            new FusedCollection("tier-a", Store(), new EmptyVectorEmbeddingService()),
        ]);

        var result = await Service(Store(), new EmptyVectorEmbeddingService(), fused)
            .SearchWithDegradationAsync(Request(SearchModes.Semantic), User, Ct);

        result.Results.Should().BeEmpty();
        result.DegradedReasons.Should().Equal(
            SearchDegradedReasons.EmbedFailed, SearchDegradedReasons.FusedEmbedFailed);
    }

    // ── A1: 段の縮退 ──────────────────────────────────────────

    [Fact]
    public async Task 再順位付けが元の順へ戻したらrerank_failedが添えられる()
    {
        var result = await Service(Store(), new CountingEmbeddingService(), reranker: new FixedReranker(degraded: true))
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.DegradedReasons.Should().Equal(SearchDegradedReasons.RerankFailed);
    }

    // 陰性対照: 段が掛けた（または設計どおり掛けなかった）なら印は立たない。
    [Fact]
    public async Task 再順位付けが縮退しなければ印は立たない()
    {
        var result = await Service(Store(), new CountingEmbeddingService(), reranker: new FixedReranker(degraded: false))
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Degraded.Should().BeFalse();
    }

    // 埋め込みと段の縮退は重なる（正の順で並ぶ）。
    [Fact]
    public async Task 埋め込みと再順位付けの縮退は正の順で並ぶ()
    {
        var result = await Service(Store(), new EmptyVectorEmbeddingService(), reranker: new FixedReranker(degraded: true))
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.DegradedReasons.Should().Equal(SearchDegradedReasons.EmbedFailed, SearchDegradedReasons.RerankFailed);
    }

    [Fact]
    public async Task 近傍展開が働かなければgraph_expand_failedが添えられる()
    {
        var store = Store();
        var result = await Graph(store, GraphNeighborhood.Unavailable)
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Results.Should().NotBeEmpty("一次の結果のまま返る");
        result.DegradedReasons.Should().Equal(SearchDegradedReasons.GraphExpandFailed);
    }

    // 陰性対照: グラフに辺が無いのは設計どおり（起点が孤立している）。縮退ではない。
    [Fact]
    public async Task グラフに辺が無いだけなら縮退ではない()
    {
        var result = await Graph(Store(), GraphNeighborhood.Empty)
            .SearchWithDegradationAsync(Request(), User, Ct);

        result.Degraded.Should().BeFalse();
    }

    // ⚠ 残余リスクの固定（[[IADR-0534]] §結果 / #1871 監査 🟡-1）: 近傍展開の起点は**露出で落とす前**の
    // ベクトル側ヒットから取る（ADR-0035 決定 2。並びを変えないため露出の後の集合へは移さない）。
    // そのため段が有効でグラフが故障しているとき、露出 OFF の文書（本人は ABAC 上読める）だけが当たると、
    // **結果は空なのに `graph-expand-failed` が立つ**。権限の有無・他人の文書は表さない。挙動を変えるなら IADR を改める。
    [Fact]
    public async Task 露出OFFの文書だけが当たりグラフが故障していると結果は空でgraph_expand_failedが立つ()
    {
        var hidden = new SearchResultDto(Guid.NewGuid(), Guid.NewGuid(), "hidden", "hidden", 1f, null,
            new()
            {
                [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                [DocumentExposure.SearchKey] = DocumentExposure.Excluded,
            }, []);
        var store = new RecordingVectorStore { VectorResults = [hidden], KeywordResults = [] };
        var expander = new FixedExpander(GraphNeighborhood.Unavailable);
        var service = new GraphExpandingSearchService(
            Service(store, new CountingEmbeddingService()), store, expander,
            new GraphExpansionOptions { Enabled = true }, NullLogger<GraphExpandingSearchService>.Instance);

        var result = await service.SearchWithDegradationAsync(Request(), User, Ct);

        expander.Calls.Should().Be(1, "起点は露出で落とす前のベクトル側ヒットから取る");
        result.Results.Should().BeEmpty("露出 OFF の文書は出口で落ちる");
        result.DegradedReasons.Should().Equal(SearchDegradedReasons.GraphExpandFailed);
    }

    // 埋め込みが落ちると起点が無く、段は呼ばれない —— 根の原因（embed-failed）だけが立つ。
    [Fact]
    public async Task 埋め込みが落ちて起点が無ければ段の印は足さない()
    {
        var expander = new FixedExpander(GraphNeighborhood.Unavailable);
        var service = new GraphExpandingSearchService(
            Service(Store(), new EmptyVectorEmbeddingService()), Store(), expander,
            new GraphExpansionOptions { Enabled = true }, NullLogger<GraphExpandingSearchService>.Instance);

        var result = await service.SearchWithDegradationAsync(Request(), User, Ct);

        expander.Calls.Should().Be(0);
        result.DegradedReasons.Should().Equal(SearchDegradedReasons.EmbedFailed);
    }

    // 従来の口（`SearchAsync`）は結果だけを返す —— 既存の呼び出し面は変わらない。
    [Fact]
    public async Task 従来の口は結果だけを返し縮退の有無で並びを変えない()
    {
        var service = Service(Store(), new EmptyVectorEmbeddingService());

        var plain = await service.SearchAsync(Request(), User, Ct);
        var detailed = await service.SearchWithDegradationAsync(Request(), User, Ct);

        plain.Select(r => r.DocumentTitle).Should().Equal(detailed.Results.Select(r => r.DocumentTitle));
    }

    // ── A4: 計数を応答の印と揃える ──────────────────────────────

    [Fact]
    public async Task 計器は応答と同じ符号で理由ごとに数える()
    {
        using var meters = new TestMeterFactory();
        var seen = new List<string>();
        using var listener = Listen(meters, seen);
        var service = Service(Store(), new EmptyVectorEmbeddingService(),
            reranker: new FixedReranker(degraded: true), metrics: new SearchDegradationMetrics(meters));

        var result = await service.SearchWithDegradationAsync(Request(), User, Ct);
        seen.Should().Equal(result.DegradedReasons, "タグの値域は応答の degradedReasons と同一");

        seen.Clear();
        await Service(Store(), new CountingEmbeddingService(), metrics: new SearchDegradationMetrics(meters))
            .SearchWithDegradationAsync(Request(), User, Ct);
        seen.Should().BeEmpty("0 が正常（縮退しない検索は数えない）");
    }

    private static GraphExpandingSearchService Graph(RecordingVectorStore store, GraphNeighborhood neighborhood) =>
        new(Service(store, new CountingEmbeddingService()), store, new FixedExpander(neighborhood),
            new GraphExpansionOptions { Enabled = true }, NullLogger<GraphExpandingSearchService>.Instance);

    private static MeterListener Listen(TestMeterFactory meters, List<string> seen)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (ReferenceEquals(i.Meter.Scope, meters) && i.Name == SearchDegradationMetrics.CounterName)
                    l.EnableMeasurementEvents(i);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == SearchDegradationMetrics.ReasonTag)
                    lock (seen) seen.Add(t.Value?.ToString() ?? "");
        });
        listener.Start();
        return listener;
    }

    private sealed class FixedReranker(bool degraded) : ISearchReranker
    {
        public Task<RerankOutcome> RerankAsync(
            SearchRequest request, string sort, List<SearchResultDto> candidates, CancellationToken ct = default)
            => Task.FromResult(new RerankOutcome(candidates, degraded));
    }

    private sealed class FixedExpander(GraphNeighborhood neighborhood) : IGraphNeighborExpander
    {
        public int Calls { get; private set; }

        public Task<GraphNeighborhood> ExpandAsync(
            IReadOnlyList<Guid> seedDocumentIds, int hops, SearchUserContext user, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(neighborhood);
        }
    }
}
