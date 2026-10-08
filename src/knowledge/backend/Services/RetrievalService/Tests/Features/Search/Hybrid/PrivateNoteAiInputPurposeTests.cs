using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Infrastructure.ExternalServices;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-19, FR-21 ⑨, FR-03, FR-04, ADR-0061 決定 3, [[IADR-0512]] (#1752):
// **検索の出口は、検索の用途の露出属性で落とす。**
//
// FR-19 は露出 3 トグルを**独立**と定め、ADR-0061 決定 3 は「消費側の各経路が**自分の**属性を見て弾く」と定めた。
// 利用者へ一覧を返す検索は「横断検索に含める」（`search_exposure`）で、RAG の文脈を集める検索は
// 「AI の入力に含める」（`ai_input`）で落とす。従前の出口は用途を問わず `search_exposure` で落としたので、
// 「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料は RAG の文脈に一度も届かなかった。
//
// 🔴 陰性の主張には陽性対照を対で置く（同じ索引・同じスコープで用途だけを変える）。
// 🔴 変異試験（実測は作業仕様書）: 出口の述語を `IsSearchAllowed` 固定へ戻すと、
//   `AI入力の用途なら横断検索OFFかつAI入力ONの個人資料が返る` ほか AI 入力の用途の陽性 3 件が落ちる。
[Trait("TestKind", "Unit")]
public class PrivateNoteAiInputPurposeTests
{
    private const string Query = "個人資料";
    private const string SearchOnly = "横断検索だけ ON の個人資料";
    private const string AiOnly = "AI 入力だけ ON の個人資料";
    private const string Both = "両方 ON の個人資料";
    private const string Organization = "組織文書";

    private static ChunkPayload Note(string title, bool search, bool ai) =>
        Chunk(title,
            (DocumentScopes.Key, DocumentScopes.PrivateNote),
            ("owner", "alice"),
            (ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted),
            (DocumentExposure.SearchKey, DocumentExposure.FromToggle(search)),
            (DocumentExposure.GraphKey, DocumentExposure.Excluded),
            (DocumentExposure.AiKey, DocumentExposure.FromToggle(ai)));

    // 露出キーを持たない組織文書（既存文書と同じ形）。どちらの用途でも出る陽性対照。
    private static ChunkPayload OrganizationDoc() =>
        Chunk(Organization, ("owner", "alice"));

    private static ChunkPayload Chunk(string title, params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} は {Query} を含む本文", [0.1f, 0.2f],
            null, attrs.ToDictionary(a => a.Key, a => a.Value), []);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    private static InMemoryVectorStore AllFour() =>
        StoreWith(Note(SearchOnly, search: true, ai: false), Note(AiOnly, search: false, ai: true),
            Note(Both, search: true, ai: true), OrganizationDoc());

    private static SearchRequest Request() =>
        new(Query, 10, null, new AccessScope([], GrantsAccess: true,
            [new AccessScopeBranch("所有者ベース", [new AttributeFilter("owner", ["alice"])])]));

    private static SearchUserContext ForSearch => TestSearchUser.FromBody("alice");
    private static SearchUserContext ForAi => TestSearchUser.FromBody("alice").ForAiInput();

    private static async Task<List<string>> TitlesAsync(IHybridSearchService search, SearchUserContext user)
        => (await search.SearchAsync(Request(), user, TestContext.Current.CancellationToken))
            .Select(r => r.DocumentTitle).ToList();

    private static HybridSearchService Plain(InMemoryVectorStore store, ISearchReranker? reranker = null) =>
        new(store, new CountingEmbeddingService(), NullLogger<HybridSearchService>.Instance, reranker: reranker);

    // FR-19, ADR-0061 決定 3（#1752 の主語）: RAG の文脈を集める検索は「AI の入力に含める」で選ぶ。
    [Fact]
    public async Task AI入力の用途なら横断検索OFFかつAI入力ONの個人資料が返る()
    {
        var titles = await TitlesAsync(Plain(AllFour()), ForAi);

        titles.Should().Contain(AiOnly,
            "FR-19 の 3 トグルは独立である —— 「横断検索に含める」OFF でも「AI の入力に含める」ON なら RAG の候補になる");
        titles.Should().Contain([Both, Organization], "陽性対照: AI 入力 ON の資料と露出キーの無い組織文書も返る");
    }

    // FR-21 ⑨ と同じ向き: AI 入力の用途は「AI の入力に含める」OFF の資料を返さない（横断検索 ON でも）。
    [Fact]
    public async Task AI入力の用途は横断検索ONでもAI入力OFFの個人資料を返さない()
    {
        var titles = await TitlesAsync(Plain(AllFour()), ForAi);

        titles.Should().NotContain(SearchOnly, "AI の入力に含めない資料は RAG の候補へ渡さない");
        titles.Should().Contain(Both, "陽性対照: 同じ所有者・同じスコープの AI 入力 ON の資料は返る");
    }

    // 🔴 横断検索の用途（既定）は従来どおり: 「横断検索に含める」OFF の資料は一覧に出ない。
    [Fact]
    public async Task 横断検索の用途は従来どおり横断検索OFFの個人資料を一覧に出さない()
    {
        var titles = await TitlesAsync(Plain(AllFour()), ForSearch);

        titles.Should().NotContain(AiOnly, "一覧に出てはならない資料が、AI 入力の用途の導入で出てはならない");
        titles.Should().Contain([SearchOnly, Both, Organization], "陽性対照: 横断検索 ON の資料と組織文書は出る");
    }

    // 🔴 用途の既定は横断検索である。入口が何も言わない利用者文脈（REST の入口・MCP のツール）はこちらへ倒れる。
    [Fact]
    public void 利用者文脈の用途の既定は横断検索でありAI入力へ切り替える口は1つだけ()
    {
        TestSearchUser.FromBody("alice").ExposureKey.Should().Be(DocumentExposure.SearchKey);
        SearchUserContext.FromRequest(new DefaultHttpContext()).ExposureKey.Should()
            .Be(DocumentExposure.SearchKey, "REST の入口は用途を受けない（呼び出し元を中継者と区別できない）");

        var ai = TestSearchUser.FromBody("alice", ("department", "dev")).ForAiInput();
        ai.ExposureKey.Should().Be(DocumentExposure.AiKey);
        ai.UserId.Should().Be("alice", "用途を変えても主体は変わらない");
        ai.Attributes.Should().Contain("department", "dev");
        ai.ForwardableCredential.Should().BeNull();
    }

    // FR-17, ADR-0035: 二段検索の出口も同じ用途で落とす（段は利用者文脈を素通しする）。
    // 近傍の辺を 1 本張り、合成の出口（3 つ目の return）を通す。
    [Fact]
    public async Task 二段検索の出口もAI入力の用途で落とす()
    {
        var aiOnly = Note(AiOnly, search: false, ai: true);
        var searchOnly = Note(SearchOnly, search: true, ai: false);
        var store = StoreWith(aiOnly, searchOnly);
        var expander = new FakeGraphExpander(
            [new GraphNeighborEdge(aiOnly.DocumentId, searchOnly.DocumentId, 1.0)]);
        var expanding = new GraphExpandingSearchService(
            Plain(store), store, expander, new GraphExpansionOptions { Enabled = true }.Normalize(),
            NullLogger<GraphExpandingSearchService>.Instance);

        var forAi = await TitlesAsync(expanding, ForAi);
        var forSearch = await TitlesAsync(expanding, ForSearch);

        forAi.Should().Equal([AiOnly], "二段検索の出口も AI 入力の用途なら ai_input で落とす");
        forSearch.Should().Equal([SearchOnly], "陽性対照: 同じ索引・同じ段で、横断検索の用途なら search_exposure で落とす");
        expander.User!.ExposureKey.Should().Be(DocumentExposure.SearchKey, "最後の呼び出しの文脈がそのまま段へ届く");
    }

    // ADR-0127 決定 3, [[IADR-0498]] 決定 4: 再順位付けの段に渡す候補も、同じ用途で先に落とす。
    [Fact]
    public async Task 再順位付けの段へ渡す候補もAI入力の用途で落とす()
    {
        var reranker = new RecordingReranker();

        var titles = await TitlesAsync(Plain(AllFour(), reranker), ForAi);

        reranker.Candidates.Select(c => c.DocumentTitle).Should()
            .BeEquivalentTo([AiOnly, Both, Organization], "段へ渡すのは返してよい候補だけ（AI 入力 OFF は送らない）");
        titles.Should().BeEquivalentTo([AiOnly, Both, Organization]);
    }

    // 🔴 [[IADR-0512]] 決定 3: gRPC 面の用途の写し。`AI_INPUT` だけが AI 入力、未指定・`SEARCH`・未知の値は横断検索。
    [Theory]
    [InlineData(Pb.ExposurePurpose.AiInput, DocumentExposure.AiKey)]
    [InlineData(Pb.ExposurePurpose.Unspecified, DocumentExposure.SearchKey)]
    [InlineData(Pb.ExposurePurpose.Search, DocumentExposure.SearchKey)]
    [InlineData((Pb.ExposurePurpose)99, DocumentExposure.SearchKey)]
    public void gRPC面の用途はAI_INPUTだけをAI入力へ写す(Pb.ExposurePurpose purpose, string expected)
    {
        DocumentSearchGrpcService.WithPurpose(TestSearchUser.FromBody("alice"), purpose)
            .ExposureKey.Should().Be(expected);
    }

    // 段の候補を記録し、そのまま返す（並べ替えない）。
    private sealed class RecordingReranker : ISearchReranker
    {
        public List<SearchResultDto> Candidates { get; } = [];

        public Task<List<SearchResultDto>> RerankAsync(
            SearchRequest request, string sort, List<SearchResultDto> candidates, CancellationToken ct = default)
        {
            Candidates.AddRange(candidates);
            return Task.FromResult(candidates);
        }
    }
}
