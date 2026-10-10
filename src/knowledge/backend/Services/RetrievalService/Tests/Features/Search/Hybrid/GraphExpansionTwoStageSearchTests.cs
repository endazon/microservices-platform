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

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-04, FR-17, UC-10, ADR-0035 決定 1・2 (#970): 二段検索の段（グラフ近傍展開と再ランク）。
//
// 段の輪郭:
//   ① 既存のハイブリッド検索 → ② ベクトル側上位 N を起点にグラフ近傍展開
//   → ③ 到達文書に絞ったベクトル検索 → ④ 重みつき合成で再ランク
[Trait("TestKind", "Integration")]
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

        var report = factory.Services.GetRequiredService<ServiceIntrospectionDto>();
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
            .Should().BeOfType<GrpcGraphNeighborExpander>();

        var report = factory.Services.GetRequiredService<ServiceIntrospectionDto>();
        report!.Ports.Should().Contain(p =>
            p.Port == "graph-expansion" && p.Implementation == nameof(GrpcGraphNeighborExpander));
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
            .SearchAsync(Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);

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
            .SearchAsync(Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);

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
            .SearchAsync(Request(scope: scope), TestSearchUser.Any, TestContext.Current.CancellationToken);

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
            Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);

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

        var baseline = await inner.SearchAsync(Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);
        var expanded = await Expanding(store, inner, new FakeGraphExpander([]))
            .SearchAsync(Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);

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
            .SearchAsync(Request(), TestSearchUser.Any, TestContext.Current.CancellationToken);

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

    // ── T-05 / T-06 / T-08 / R-01〜R-03 / U-02（撤去。［2026-10-10 / #1255］[[IADR-0533]] 決定 4） ──
    //
    // 近傍展開の REST 実装（`GraphServiceNeighborExpander`。利用者の `Authorization` を GraphService へ転送する方式 A）を
    // 撤去したので、その輸送に固有の表明（ヘッダの転送・資格情報が無ければ呼ばない・404 の存在秘匿・応答の `nodes` を読まない・
    // 辞書の実重み／フォールバック重み）を外した。gRPC 実装の同じ性質は `GrpcGraphNeighborExpanderTests` が持つ
    // （未認証なら 1 度も呼ばない・見えない起点は空・辞書の実重みとフォールバック重み・利用者のトークンを載せない）。
    // 段そのもの（出典化・ABAC との AND・起点・空集合・合成）は上の T-01〜T-04・T-07・T-09〜T-12 が FakeGraphExpander で測る。

    // ── U-01〜U-03: 利用者文脈は入口が決めて段まで引数で運ぶ（[[IADR-0426]] 決定 2 / #1255） ──

    // 🔴 U-01（本スライスの核心）: **入口が決めた利用者文脈が、そのまま近傍展開へ渡る。**
    // 段が器（`IHttpContextAccessor`）から拾い直していると、east-west gRPC の入口では
    // **呼び出し元サービスの s2s 主体**が ABAC の主体に化ける —— 例外は 1 つも起きず、
    // グラフ展開だけが静かに空になる（あるいは他人の権限で広がる）。
    [Fact]
    public async Task 入口が決めた利用者文脈がそのまま近傍展開へ渡る()
    {
        var seedDoc = Guid.NewGuid();
        var (store, inner) = Stage(Chunk(seedDoc, "起点 文書", [1f, 0f]));
        store.VectorSideDocuments.Add(seedDoc);
        var expander = new FakeGraphExpander([]);
        var user = SearchUserContext.FromBody(
            "alice", new Dictionary<string, string> { ["department"] = "hr" });

        await Expanding(store, inner, expander)
            .SearchAsync(Request(), user, TestContext.Current.CancellationToken);

        expander.User.Should().NotBeNull("段は利用者文脈を受け取っている");
        expander.User!.UserId.Should().Be("alice");
        expander.User.Attributes.Should().Contain(
            new KeyValuePair<string, string>("department", "hr"));
        expander.User.ForwardableCredential.Should().BeNull(
            "east-west gRPC の入口には転送できる利用者の資格情報が無い");
    }

    // ★ U-01 の陽性対照。REST の入口では**転送できる資格情報が付いて**運ばれる
    //（「常に null」の実装を落とす）。
    [Fact]
    public async Task REST入口では転送できる資格情報が付いて運ばれる()
    {
        var seedDoc = Guid.NewGuid();
        var (store, inner) = Stage(Chunk(seedDoc, "起点 文書", [1f, 0f]));
        store.VectorSideDocuments.Add(seedDoc);
        var expander = new FakeGraphExpander([]);

        await Expanding(store, inner, expander)
            .SearchAsync(Request(), UserWithCredential(), TestContext.Current.CancellationToken);

        expander.User!.ForwardableCredential.Should().Be(AllowedToken);
    }

    // 🔴 [[IADR-0426]] 決定 2 (#1255): 方式 A の転送は**入口が決めた利用者文脈**が運ぶ。
    // 従前は `IHttpContextAccessor` から `Authorization` を拾っていたが、その形のままだと
    // east-west gRPC の入口で**呼び出し元サービスの s2s トークン**を転送してしまう。
    private static SearchUserContext UserWithCredential(
        string authorization = AllowedToken)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        ctx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice")],
                "test"));
        return SearchUserContext.FromRequest(ctx);
    }

    private const string AllowedToken = "Bearer allowed";
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

// 近傍展開ポートの記録用スタブ（起点・ホップ数・**受け取った利用者文脈**を観測する）。
// 🔴 利用者文脈を記録するのは [[IADR-0426]] 決定 2 のためである ——
// 段が器から拾い直していないこと（入口が決めた主体がそのまま届くこと）を測る。
internal sealed class FakeGraphExpander(IReadOnlyList<GraphNeighborEdge> edges) : IGraphNeighborExpander
{
    public List<Guid> Seeds { get; } = [];
    public int Hops { get; private set; }
    public SearchUserContext? User { get; private set; }

    public Task<GraphNeighborhood> ExpandAsync(
        IReadOnlyList<Guid> seedDocumentIds, int hops, SearchUserContext user,
        CancellationToken ct = default)
    {
        Seeds.AddRange(seedDocumentIds);
        Hops = hops;
        User = user;
        return Task.FromResult(new GraphNeighborhood(edges));
    }
}

// 段を有効にした宿主。**構成（GraphExpansion:Enabled）だけで段が入る**ことを確かめるため、
// `IHybridSearchService` の差し替えは行わない（本番と同じ登録経路を通す）。
internal class GraphExpansionFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 🔴 **`UseSetting` で渡す。** 段の有無は `Program.cs` が **`builder.Build()` の前**に
        // 読む値であり、`ConfigureAppConfiguration` で足した構成はそこまでに間に合わない
        // （足しても既定オフのまま起動し、試験が「段が入らない」で落ちる）。
        builder.UseSetting("GraphExpansion:Enabled", "true");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore, StagedVectorStore>();

            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService>(new FixedEmbeddingService([1f, 0f]));
        });
    }
}
