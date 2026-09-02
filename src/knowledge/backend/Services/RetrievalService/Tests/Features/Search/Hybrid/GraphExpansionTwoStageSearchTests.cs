using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Domain.Ports;
using RetrievalService.Domain;
using RetrievalService.Features.Search.Hybrid;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-04, FR-17, UC-10, ADR-0035 決定 1・2 (#970): 二段検索の段（グラフ近傍展開と再ランク）。
//
// 段の輪郭:
//   ① 既存のハイブリッド検索 → ② ベクトル側上位 N を起点にグラフ近傍展開
//   → ③ 到達文書に絞ったベクトル検索 → ④ 重みつき合成で再ランク
public class GraphExpansionTwoStageSearchTests
{
    private static readonly float[] QueryVector = [1f, 0f];

    // ── 素材 ───────────────────────────────────────────────────────
    private static ChunkPayload Chunk(
        Guid documentId, string text, float[] vector, Dictionary<string, string>? attrs = null) =>
        new(Guid.NewGuid(), documentId, $"doc:{text}", text, vector,
            $"s3://bucket/{documentId}.md", attrs ?? [], []);

    private static GraphNeighborEdge Edge(Guid from, Guid to, double weight) => new(from, to, weight);

    private static SearchRequest Request(string query = "検索語", int topK = 10, AccessScope? scope = null) =>
        new(query, topK, null, scope ?? new AccessScope([], GrantsAccess: true));

    private static (StagedVectorStore Store, HybridSearchService Inner) Stage(params ChunkPayload[] chunks)
    {
        var store = new StagedVectorStore();
        foreach (var c in chunks)
            store.UpsertAsync(c).GetAwaiter().GetResult();
        var inner = new HybridSearchService(
            store, new FixedEmbeddingService(QueryVector), NullLogger<HybridSearchService>.Instance);
        return (store, inner);
    }

    private static GraphExpandingSearchService Expanding(
        StagedVectorStore store, HybridSearchService inner, IGraphNeighborExpander expander,
        GraphExpansionOptions? options = null) =>
        new(inner, store, expander, (options ?? new GraphExpansionOptions { Enabled = true }).Normalize(),
            NullLogger<GraphExpandingSearchService>.Instance);

    // ── T-01 / T-02: 既定オフと opt-in ─────────────────────────────

    // FR-04, FR-14, FR-17, ADR-0035 決定 2, ADR-0018: 🔴 **構成を与えない状態では段が付かない。**
    // 段は DI に存在せず（フラグ分岐ではなく型として不在）、自己申告にも現れない。
    [Fact]
    public async Task 構成なしでは二段検索の段が付かない()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHybridSearchService>()
            .Should().BeOfType<HybridSearchService>("既定オフ（ADR-0035 決定 2）である");
        scope.ServiceProvider.GetService<IGraphNeighborExpander>()
            .Should().BeNull("段が無い構成では近傍展開のポートごと登録されない");

        var report = await factory.CreateClient().GetFromJsonAsync<ServiceIntrospectionDto>(
            "/internal/introspection", TestContext.Current.CancellationToken);
        report!.Ports.Select(p => p.Port).Should().NotContain("graph-expansion");
    }

    // FR-04, FR-14, FR-17, ADR-0035 決定 2, ADR-0018: opt-in で段が入り、**外から読める**
    // （A/B 比較は応答の形では区別できないため、自己申告が唯一の手掛かりである）。T-01 の陽性対照。
    [Fact]
    public async Task Optinで段が入り自己申告に現れる()
    {
        await using var factory = new GraphExpansionFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHybridSearchService>()
            .Should().BeOfType<GraphExpandingSearchService>();
        scope.ServiceProvider.GetService<IGraphNeighborExpander>()
            .Should().BeOfType<GraphServiceNeighborExpander>();

        var report = await factory.CreateClient().GetFromJsonAsync<ServiceIntrospectionDto>(
            "/internal/introspection", TestContext.Current.CancellationToken);
        report!.Ports.Should().Contain(p =>
            p.Port == "graph-expansion" && p.Implementation == nameof(GraphServiceNeighborExpander));
    }

    // ── T-03 / T-04: 出典化とスコアの意味 ──────────────────────────

    // FR-04, FR-17, UC-10, ADR-0035 決定 1: グラフ由来の文書が**チャンク単位の出典**として現れる
    // （ノードのままでは ChunkId / Score / Snippet を持てない。段③が正規の経路で与える）。
    [Fact]
    public async Task グラフ由来の文書がチャンク単位の出典として現れる()
    {
        var seedDoc = Guid.NewGuid();
        var neighborDoc = Guid.NewGuid();
        var neighborChunk = Chunk(neighborDoc, "近傍 文書", [1f, 0f]);
        var (store, inner) = Stage(Chunk(seedDoc, "起点 文書", [1f, 0f]), neighborChunk);
        store.VectorSideDocuments.Add(seedDoc);

        var results = await Expanding(store, inner,
                new FakeGraphExpander([Edge(seedDoc, neighborDoc, 1.0)]))
            .SearchAsync(Request(), TestContext.Current.CancellationToken);

        var hit = results.Single(r => r.DocumentId == neighborDoc);
        hit.ChunkId.Should().Be(neighborChunk.ChunkId);
        hit.Text.Should().NotBeNullOrEmpty("スニペットは段③のチャンクから来る");
        hit.MarkdownUri.Should().NotBeNullOrEmpty();
    }

    // FR-04, ADR-0035 決定 1: 🔴 **グラフの近接度を `Score` に混ぜない。**
    // 返るスコアはベクトルストアが返した類似度そのものであり、合成値ではない。
    [Fact]
    public async Task グラフ由来チャンクのScoreに近接度が混ざらない()
    {
        var seedDoc = Guid.NewGuid();
        var neighborDoc = Guid.NewGuid();
        var (store, inner) = Stage(
            Chunk(seedDoc, "起点 文書", [1f, 0f]),
            Chunk(neighborDoc, "近傍 文書", [1f, 0f]));
        store.VectorSideDocuments.Add(seedDoc);

        var results = await Expanding(store, inner,
                new FakeGraphExpander([Edge(seedDoc, neighborDoc, 1.0)]))
            .SearchAsync(Request(), TestContext.Current.CancellationToken);

        var hit = results.Single(r => r.DocumentId == neighborDoc);
        var storeScore = store.LastWithinDocumentsResults.Single(r => r.DocumentId == neighborDoc).Score;
        hit.Score.Should().Be(storeScore, "出典のスコアは類似度である（近接度を足し込まない）");

        // 合成値は並べ替えにしか使わない。**同じ値であってはならない**ことを陽に測る。
        GraphRerank.Compose(GraphRerank.RankScore(0), proximity: 1.0, searchWeight: 1.0, graphWeight: 0.35)
            .Should().NotBe(hit.Score);
    }

    // ── T-07: ABAC との AND（多層防御） ────────────────────────────

    // FR-05, FR-17, ADR-0034, IADR-0259 決定 3: 🔴 **文書 ID の制約は ABAC を置き換えない。**
    // グラフが権限外の文書を返しても、段③の ABAC フィルタで落ちる（否定形＋陽性対照の対）。
    [Fact]
    public async Task グラフが返した権限外文書は段3のABACで落ちる()
    {
        var seedDoc = Guid.NewGuid();
        var allowedDoc = Guid.NewGuid();
        var forbiddenDoc = Guid.NewGuid();
        var (store, inner) = Stage(
            Chunk(seedDoc, "起点", [1f, 0f], new() { ["confidentiality"] = "internal" }),
            Chunk(allowedDoc, "権限内 近傍", [1f, 0f], new() { ["confidentiality"] = "internal" }),
            Chunk(forbiddenDoc, "権限外 近傍", [1f, 0f], new() { ["confidentiality"] = "restricted" }));
        store.VectorSideDocuments.Add(seedDoc);

        var scope = new AccessScope([new AttributeFilter("confidentiality", ["internal"])], GrantsAccess: true);
        var results = await Expanding(store, inner, new FakeGraphExpander(
                [Edge(seedDoc, allowedDoc, 1.0), Edge(seedDoc, forbiddenDoc, 1.0)]))
            .SearchAsync(Request(scope: scope), TestContext.Current.CancellationToken);

        results.Select(r => r.DocumentId).Should().Contain(allowedDoc, "陽性対照（権限内は現れる）");
        results.Select(r => r.DocumentId).Should().NotContain(forbiddenDoc, "権限外はグラフ経由でも現れない");
    }

    // ── T-09 / T-10: 起点と空集合 ──────────────────────────────────

    // FR-17, ADR-0035 決定 2: 展開の起点は**ベクトル検索の上位 N 件のみ**。
    // 全文検索側だけに現れた文書は起点にならない。
    [Fact]
    public async Task 展開の起点はベクトル側の上位N件だけである()
    {
        var vectorDoc = Guid.NewGuid();
        var keywordDoc = Guid.NewGuid();
        var (store, inner) = Stage(
            Chunk(vectorDoc, "検索語 を 含む", [1f, 0f]),
            Chunk(keywordDoc, "検索語 だけ 一致", [0f, 1f]));
        store.VectorSideDocuments.Add(vectorDoc);   // 全文側は両方に当たる（語が一致するため）

        var expander = new FakeGraphExpander([]);
        await Expanding(store, inner, expander).SearchAsync(
            Request(), TestContext.Current.CancellationToken);

        expander.Seeds.Should().Equal([vectorDoc]);
        expander.Hops.Should().Be(GraphExpansionOptions.DefaultHops, "既定 2・上限 3（ADR-0034 決定 3）");
    }

    // FR-17, IADR-0259 決定 2: グラフが 0 件なら段③を呼ばない。
    // 🔴 **空集合を「全件」と読むと、検索が全文書へ広がる。** その経路が存在しないことを固定する。
    [Fact]
    public async Task グラフが0件なら段3を呼ばず結果は既存検索と一致する()
    {
        var seedDoc = Guid.NewGuid();
        var (store, inner) = Stage(Chunk(seedDoc, "起点 文書", [1f, 0f]), Chunk(Guid.NewGuid(), "無関係", [1f, 0f]));
        store.VectorSideDocuments.Add(seedDoc);

        var baseline = await inner.SearchAsync(Request(), TestContext.Current.CancellationToken);
        var expanded = await Expanding(store, inner, new FakeGraphExpander([]))
            .SearchAsync(Request(), TestContext.Current.CancellationToken);

        store.WithinDocumentsCalls.Should().Be(0);
        expanded.Select(r => r.ChunkId).Should().Equal(baseline.Select(r => r.ChunkId));
    }

    // FR-03, ADR-0035 決定 1: 段が付いても**埋め込みは 1 回だけ**呼ぶ（既存検索をやり直さない）。
    // 中間値を返す内部口（SearchDetailedAsync）を足した理由がここにある。
    [Fact]
    public async Task 段が付いても埋め込みの呼び出しは1回だけである()
    {
        var seedDoc = Guid.NewGuid();
        var neighborDoc = Guid.NewGuid();
        var store = new StagedVectorStore();
        await store.UpsertAsync(Chunk(seedDoc, "起点", [1f, 0f]), TestContext.Current.CancellationToken);
        await store.UpsertAsync(Chunk(neighborDoc, "近傍", [1f, 0f]), TestContext.Current.CancellationToken);
        store.VectorSideDocuments.Add(seedDoc);
        var embedding = new CountingFixedEmbeddingService(QueryVector);
        var inner = new HybridSearchService(store, embedding, NullLogger<HybridSearchService>.Instance);

        await Expanding(store, inner, new FakeGraphExpander([Edge(seedDoc, neighborDoc, 1.0)]))
            .SearchAsync(Request(), TestContext.Current.CancellationToken);

        embedding.Calls.Should().Be(1);
    }

    // ── T-12: 重みつきの合成（純関数） ────────────────────────────

    // FR-17, ADR-0035 決定 2: 辺の型の重みが再ランクに効く。
    // `supersedes`(1.0) 経由は減衰せず、`related`(0.3) 経由より上位になる。
    [Fact]
    public void 辺の型の重みが近接度に効く()
    {
        var seed = Guid.NewGuid();
        var strong = Guid.NewGuid();
        var weak = Guid.NewGuid();
        var far = Guid.NewGuid();

        var proximity = GraphProximity.From(
            [seed],
            [Edge(seed, strong, 1.0), Edge(seed, weak, 0.3), Edge(weak, far, 0.3)],
            hops: 2);

        proximity[strong].Should().BeGreaterThan(proximity[weak], "supersedes は強く誘導する");
        proximity[far].Should().BeApproximately(0.09, 1e-9, "related を 2 ホップ辿ると急速に減衰する");
        proximity.Should().NotContainKey(seed, "起点自身は近接度を持たない（ベクトル側の信号を二重に数えない）");

        // 合成: 同じ順位なら近接度の大きい方が上に来る（重みつきの合成であることの確認）。
        GraphRerank.Compose(GraphRerank.RankScore(3), proximity[strong], 1.0, 0.35)
            .Should().BeGreaterThan(GraphRerank.Compose(GraphRerank.RankScore(3), proximity[weak], 1.0, 0.35));
    }

    // FR-17, ADR-0034 決定 3: ホップ数の構成は範囲外なら既定（2）へ縮退する（例外にしない）。
    [Theory]
    [InlineData(0, GraphExpansionOptions.DefaultHops)]
    [InlineData(4, GraphExpansionOptions.DefaultHops)]
    [InlineData(3, 3)]
    public void ホップ数の構成は範囲外なら既定へ縮退する(int configured, int expected) =>
        new GraphExpansionOptions { Hops = configured }.Normalize().Hops.Should().Be(expected);

    // ── T-05 / T-06: 権限伝播（否定形と陽性対照） ─────────────────

    // FR-05, FR-17, ADR-0034, #916a: 🔴 **否定形。** 資格情報の無い検索では GraphService を呼ばない
    // （呼ぶと全ホップが 404 に落ち、「グラフには何も無い」と読める静かな故障になる）。
    [Fact]
    public async Task Authorizationが無ければGraphServiceを呼ばない()
    {
        var graph = new FakeGraphHandler();
        await using var factory = new GraphExpansionFactory(graph);
        await SeedAsync(factory, graph);

        var resp = await factory.CreateClient().PostAsJsonAsync(
            "/search", Request(), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        graph.Requests.Should().BeEmpty("資格情報が無いなら下流を呼ばない");
        body!.Results.Select(r => r.DocumentId).Should().NotContain(graph.NeighborDocumentId);
    }

    // FR-05, FR-17, ADR-0034, #916a: 🔴 **陽性対照。** 呼び出し元の `Authorization` が
    // **そのまま** GraphService へ伝播し（方式 A）、グラフ由来の根拠が現れる。
    // 否定形だけでは「常に呼ばない実装」を通してしまう。
    [Fact]
    public async Task Authorizationを伝播してグラフ由来の根拠が現れる()
    {
        var graph = new FakeGraphHandler();
        await using var factory = new GraphExpansionFactory(graph);
        await SeedAsync(factory, graph);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", FakeGraphHandler.AllowedToken);
        var resp = await client.PostAsJsonAsync("/search", Request(), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        graph.Requests.Should().NotBeEmpty();
        graph.Requests.Should().OnlyContain(r => r.Authorization == FakeGraphHandler.AllowedToken,
            "本文で scope を渡す方式 B ではなく、ヘッダをそのまま伝播する（方式 A）");
        body!.Results.Select(r => r.DocumentId).Should().Contain(graph.NeighborDocumentId,
            "グラフ由来の文書が出典として現れる");
    }

    // FR-05, FR-17, ADR-0034: 権限外の起点は GraphService が 404（存在秘匿）を返す。
    // その場合に**グラフ由来の根拠が 1 件も出ない**ことを固定する（否定形）。
    [Fact]
    public async Task 権限外の資格情報では404となりグラフ由来の根拠が出ない()
    {
        var graph = new FakeGraphHandler();
        await using var factory = new GraphExpansionFactory(graph);
        await SeedAsync(factory, graph);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer denied");
        var resp = await client.PostAsJsonAsync("/search", Request(), TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        graph.Requests.Should().NotBeEmpty("呼びはする（拒否するのは GraphService 側である）");
        body!.Results.Select(r => r.DocumentId).Should().NotContain(graph.NeighborDocumentId);
    }

    // ── T-08: 候補の入口は辺だけ ──────────────────────────────────

    // FR-18, FR-17, ADR-0033 決定 10 (#914): 🔴 **未承認（pending / rejected）の AI 提案は
    // 根拠に現れない。** 提案は辺として存在せず、本段の候補の入口は**辺だけ**である
    // （応答の `nodes` を読まない）。構造的に混ざり得ないことをここで固定する。
    [Fact]
    public async Task 辺で到達しない文書は候補にならない()
    {
        var seed = Guid.NewGuid();
        var suggested = Guid.NewGuid();
        var graph = new FakeGraphHandler
        {
            // 提案どまりの文書がノード一覧にだけ載っている応答（辺は 1 本も無い）。
            Body = $$"""
            {"nodes":[{"documentId":"{{seed}}","title":"起点"},
                      {"documentId":"{{suggested}}","title":"提案どまり"}],
             "edges":[],"truncated":false,"totalNodes":2,"totalEdges":0,"totalIsLowerBound":false}
            """,
        };

        var expander = new GraphServiceNeighborExpander(
            new SingleClientFactory(graph, GraphExpansionFactory.GraphBaseAddress),
            AccessorWith(FakeGraphHandler.AllowedToken),
            NullLogger<GraphServiceNeighborExpander>.Instance);

        var neighborhood = await expander.ExpandAsync([seed], 2, TestContext.Current.CancellationToken);

        neighborhood.Edges.Should().BeEmpty();
        GraphProximity.From([seed], neighborhood.Edges, 2).Should().NotContainKey(suggested);
    }

    // ── R-01〜R-03: 辺の型の実重み（#970 完成。IADR-0263 決定 6 の解消） ──

    private GraphServiceNeighborExpander ExpanderOver(FakeGraphHandler graph) => new(
        new SingleClientFactory(graph, GraphExpansionFactory.GraphBaseAddress),
        AccessorWith(FakeGraphHandler.AllowedToken),
        NullLogger<GraphServiceNeighborExpander>.Instance);

    // FR-04, FR-17, ADR-0035 決定 2: R-01 辞書（`/graph/edge-types/catalog`）の**実重み**が辺に載り、
    // 重い型（1.0）経由が軽い型（0.3）経由より近接度で上回る（固定値 0.5 へ戻す変異で赤になる）。
    [Fact]
    public async Task 辞書の実重みが辺に載り再ランクに効く()
    {
        var seed = Guid.NewGuid();
        var strongDoc = Guid.NewGuid();
        var weakDoc = Guid.NewGuid();
        var strongType = Guid.NewGuid();
        var weakType = Guid.NewGuid();
        var graph = new FakeGraphHandler
        {
            StrongTypeId = strongType,
            WeakTypeId = weakType,
            Body = $$"""
            {"nodes":[],"edges":[
              {"id":"{{Guid.NewGuid()}}","sourceDocumentId":"{{seed}}","targetDocumentId":"{{strongDoc}}",
               "edgeTypeId":"{{strongType}}","provenance":"User"},
              {"id":"{{Guid.NewGuid()}}","sourceDocumentId":"{{seed}}","targetDocumentId":"{{weakDoc}}",
               "edgeTypeId":"{{weakType}}","provenance":"User"}],
             "truncated":false,"totalNodes":0,"totalEdges":2,"totalIsLowerBound":false}
            """,
        };

        var neighborhood = await ExpanderOver(graph)
            .ExpandAsync([seed], 2, TestContext.Current.CancellationToken);

        neighborhood.Edges.Should().ContainSingle(e => e.TargetDocumentId == strongDoc)
            .Which.Weight.Should().Be(1.0, "supersedes は辞書の実重みで運ばれる");
        neighborhood.Edges.Should().ContainSingle(e => e.TargetDocumentId == weakDoc)
            .Which.Weight.Should().Be(0.3, "related は辞書の実重みで運ばれる");

        // 重み差がそのまま再ランクの近接度の差になる（合成経路は T-12 が固定済み）。
        var proximity = GraphProximity.From([seed], neighborhood.Edges, 2);
        proximity[strongDoc].Should().BeGreaterThan(proximity[weakDoc],
            "型ごとの重み付け（ADR-0035 決定 2）が実際に効いている");
    }

    // R-02: 辞書に無い型の辺はフォールバック値（0.5）で扱う（例外にも 0 にもしない）。
    [Fact]
    public async Task 辞書に無い型の辺はフォールバック重みで扱う()
    {
        var seed = Guid.NewGuid();
        var neighbor = Guid.NewGuid();
        var graph = new FakeGraphHandler
        {
            Body = $$"""
            {"nodes":[],"edges":[
              {"id":"{{Guid.NewGuid()}}","sourceDocumentId":"{{seed}}","targetDocumentId":"{{neighbor}}",
               "edgeTypeId":"{{Guid.NewGuid()}}","provenance":"User"}],
             "truncated":false,"totalNodes":0,"totalEdges":1,"totalIsLowerBound":false}
            """,
        };

        var neighborhood = await ExpanderOver(graph)
            .ExpandAsync([seed], 2, TestContext.Current.CancellationToken);

        neighborhood.Edges.Should().ContainSingle()
            .Which.Weight.Should().Be(GraphServiceNeighborExpander.FallbackEdgeWeight,
                "辞書と探索の間で型が消えても検索は落とさず、中庸の重みで続ける");
    }

    // R-03: 辞書が引けない（非 2xx）でも検索は成立し、全辺フォールバック重みで縮退する。
    [Fact]
    public async Task 辞書が引けなくても全辺フォールバック重みで検索は成立する()
    {
        var graph = new FakeGraphHandler { CatalogStatusCode = HttpStatusCode.InternalServerError };

        var neighborhood = await ExpanderOver(graph)
            .ExpandAsync([graph.SeedDocumentId], 2, TestContext.Current.CancellationToken);

        // 既定 Body の辺は強い型（1.0）だが、辞書が引けないので 0.5 へ縮退する。
        neighborhood.Edges.Should().ContainSingle()
            .Which.Weight.Should().Be(GraphServiceNeighborExpander.FallbackEdgeWeight,
                "辞書の不調は無差別（中庸）への縮退であって、検索の失敗ではない");
    }

    // ── 補助 ───────────────────────────────────────────────────────

    private static async Task SeedAsync(GraphExpansionFactory factory, FakeGraphHandler graph)
    {
        var store = (StagedVectorStore)factory.Services.GetRequiredService<IVectorStore>();
        await store.UpsertAsync(Chunk(graph.SeedDocumentId, "起点 文書", [1f, 0f]),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(Chunk(graph.NeighborDocumentId, "近傍 文書", [1f, 0f]),
            TestContext.Current.CancellationToken);
        store.VectorSideDocuments.Add(graph.SeedDocumentId);
    }

    private static IHttpContextAccessor AccessorWith(string authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        return new HttpContextAccessor { HttpContext = ctx };
    }
}

// FR-03, FR-04 (#970): 段①（ベクトル側）を**指定した文書だけに絞れる**ストア。
//
// 素の `InMemoryVectorStore.SearchAsync` は登録済みの全チャンクを返すため、
// 「グラフ経由でしか到達しない文書」を作れない（段の効きが測れない）。ABAC・コサイン類似度・
// 文書 ID 制約の意味論は**素の実装へ委譲する**（テスト用に別解釈を持たない）。
internal sealed class StagedVectorStore : IVectorStore
{
    private readonly InMemoryVectorStore _inner = new();

    // 段①のベクトル側に出す文書（空なら素の実装のまま）。
    public HashSet<Guid> VectorSideDocuments { get; } = [];

    public int WithinDocumentsCalls { get; private set; }
    public ScopeFilter? LastWithinDocumentsFilters { get; private set; }
    public List<SearchResultDto> LastWithinDocumentsResults { get; private set; } = [];

    public async Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK, ScopeFilter? filters, CancellationToken ct = default)
    {
        var hits = await _inner.SearchAsync(queryVector, topK, filters, ct);
        return VectorSideDocuments.Count == 0
            ? hits
            : [.. hits.Where(h => VectorSideDocuments.Contains(h.DocumentId))];
    }

    public Task<List<SearchResultDto>> KeywordSearchAsync(
        string query, int topK, ScopeFilter? filters, CancellationToken ct = default)
        => _inner.KeywordSearchAsync(query, topK, filters, ct);

    public async Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
        float[] queryVector, int topK, IReadOnlyCollection<Guid> documentIds,
        ScopeFilter? filters, CancellationToken ct = default)
    {
        WithinDocumentsCalls++;
        LastWithinDocumentsFilters = filters;
        LastWithinDocumentsResults =
            await _inner.SearchWithinDocumentsAsync(queryVector, topK, documentIds, filters, ct);
        return LastWithinDocumentsResults;
    }

    public Task<List<string>> ListAttributeValuesAsync(
        string payloadKey, ScopeFilter? filters, CancellationToken ct = default)
        => _inner.ListAttributeValuesAsync(payloadKey, filters, ct);

    public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default) => _inner.UpsertAsync(chunk, ct);

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
        => _inner.DeleteByDocumentAsync(documentId, ct);
}

// 固定ベクトルを返す埋め込みスタブ（ゼロベクトルだと段③のコサイン類似度が全件 0 になる）。
internal sealed class FixedEmbeddingService(float[] vector) : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(vector);
}

// 同上＋呼び出し回数を数える（段が付いても埋め込みを 2 度呼ばないことの確認用）。
internal sealed class CountingFixedEmbeddingService(float[] vector) : IEmbeddingService
{
    public int Calls { get; private set; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(vector);
    }
}

// 近傍展開ポートの記録用スタブ（起点・ホップ数を観測する）。
internal sealed class FakeGraphExpander(IReadOnlyList<GraphNeighborEdge> edges) : IGraphNeighborExpander
{
    public List<Guid> Seeds { get; } = [];
    public int Hops { get; private set; }

    public Task<GraphNeighborhood> ExpandAsync(
        IReadOnlyList<Guid> seedDocumentIds, int hops, CancellationToken ct = default)
    {
        Seeds.AddRange(seedDocumentIds);
        Hops = hops;
        return Task.FromResult(new GraphNeighborhood(edges));
    }
}

// GraphService の代役。**`Authorization` を見て応答を変える** —— 権限伝播が効いているかを、
// 「後段が効いているから効く」ではなく RetrievalService の側から測るための装置である。
// 辺の型辞書（`/graph/edge-types/catalog`）も演じる（#970: 再ランクの実重みの供給元）。
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    public const string AllowedToken = "Bearer allowed";

    public Guid SeedDocumentId { get; } = Guid.NewGuid();
    public Guid NeighborDocumentId { get; } = Guid.NewGuid();

    // 辞書に載る 2 つの型（強 1.0 / 弱 0.3。ADR-0035 決定 2 の名指しの写し）。
    public Guid StrongTypeId { get; init; } = Guid.NewGuid();
    public Guid WeakTypeId { get; init; } = Guid.NewGuid();

    // 既定の応答: 起点 → 近傍の辺が 1 本（強い型）。
    public string? Body { get; init; }

    // 辞書の応答（既定: 強・弱の 2 型）。非 2xx を返す縮退の検証用に差し替え可能。
    public string? CatalogBody { get; init; }
    public HttpStatusCode CatalogStatusCode { get; init; } = HttpStatusCode.OK;

    public List<(string? Authorization, string Path)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.TryGetValues("Authorization", out var values)
            ? string.Join(' ', values)
            : null;
        Requests.Add((authorization, request.RequestUri!.PathAndQuery));

        // ADR-0034 決定 2: 非許可・不存在はすべて同一の 404（存在秘匿）。
        if (authorization != AllowedToken)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        if (request.RequestUri!.AbsolutePath == "/graph/edge-types/catalog")
        {
            if (CatalogStatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(CatalogStatusCode));

            var catalog = CatalogBody ?? $$"""
            [{"id":"{{StrongTypeId}}","name":"supersedes","layer":"core","isSymmetric":false,"weight":1.0},
             {"id":"{{WeakTypeId}}","name":"related","layer":"core","isSymmetric":true,"weight":0.3}]
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(catalog, Encoding.UTF8, "application/json"),
            });
        }

        var body = Body ?? $$"""
        {"nodes":[{"documentId":"{{SeedDocumentId}}","title":"起点"},
                  {"documentId":"{{NeighborDocumentId}}","title":"近傍"}],
         "edges":[{"id":"{{Guid.NewGuid()}}","sourceDocumentId":"{{SeedDocumentId}}",
                   "targetDocumentId":"{{NeighborDocumentId}}",
                   "edgeTypeId":"{{StrongTypeId}}","provenance":"User"}],
         "truncated":false,"totalNodes":2,"totalEdges":1,"totalIsLowerBound":false}
        """;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

// 名前付きクライアントを 1 本だけ返すファクトリ（アダプタ単体の試験用）。
internal sealed class SingleClientFactory(HttpMessageHandler handler, string baseAddress) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };
}

// 段を有効にした宿主。**構成（GraphExpansion:Enabled）だけで段が入る**ことを確かめるため、
// `IHybridSearchService` の差し替えは行わない（本番と同じ登録経路を通す）。
internal class GraphExpansionFactory(FakeGraphHandler? graph = null) : TestWebApplicationFactory
{
    public const string GraphBaseAddress = "http://graph-service.test";

    private readonly FakeGraphHandler _graph = graph ?? new FakeGraphHandler();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 🔴 **`UseSetting` で渡す。** 段の有無は `Program.cs` が **`builder.Build()` の前**に
        // 読む値であり、`ConfigureAppConfiguration` で足した構成はそこまでに間に合わない
        // （足しても既定オフのまま起動し、試験が「段が入らない」で落ちる）。
        builder.UseSetting("GraphExpansion:Enabled", "true");
        builder.UseSetting("Services:GraphService", GraphBaseAddress);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore, StagedVectorStore>();

            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService>(new FixedEmbeddingService([1f, 0f]));

            services.AddHttpClient(GraphServiceNeighborExpander.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _graph);
        });
    }
}
