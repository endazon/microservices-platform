using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Features.Search.Hybrid;

// FR-19, FR-03, FR-21, UC-11, ADR-0036 D-05・D-06, ADR-0061 決定 3・5・6, [[IADR-0396]] 決定 6 (#1184):
// **索引に載った個人資料が、誰にどう見えるか。**
//
// 計画 `ADR-0061` の裁定のうち本ファイルが測るのは 3 つである。
//   決定 3: 用途の別は索引を分けずに文書属性で表す（＝グラフ用途で載った資料は検索に出ない）
//   決定 5: 判定軸は `doc_scope` / `owner` / `shared_with` / `confidentiality` / 露出の投影
//   決定 6: 🔴 **`confidentiality` だけで判定してはならない**
//
// 🔴 **陰性（見えない）の主張には陽性対照を対で置く。** 「結果 0 件」は索引が空でも通る。
[Trait("TestKind", "Unit")]
public class PrivateNoteIndexExposureTests
{
    private const string Query = "個人資料";

    // 「横断検索に含める」ON の個人資料。所有者 alice・共有先 bob・機密区分 restricted。
    private static ChunkPayload AliceNote(string title = "alice の個人資料") =>
        Chunk(title, ["bob"],
            (DocumentScopes.Key, DocumentScopes.PrivateNote),
            ("owner", "alice"),
            (ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted),
            (DocumentExposure.SearchKey, DocumentExposure.Included),
            (DocumentExposure.GraphKey, DocumentExposure.Excluded),
            (DocumentExposure.AiKey, DocumentExposure.Excluded));

    // 陽性対照に使う組織文書（restricted）。**露出キーを持たない**（既存文書と同じ形）。
    private static ChunkPayload OrganizationDoc() =>
        Chunk("組織の個人資料ではない文書", null,
            (ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted));

    private static ChunkPayload Chunk(string title, List<string>? sharedWith,
        params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} は {Query} を含む本文", [0.1f, 0.2f],
            null, attrs.ToDictionary(a => a.Key, a => a.Value), [], SharedWith: sharedWith);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    private static Task<List<SearchResultDto>> SearchAsync(InMemoryVectorStore store,
        params AccessScopeBranch[] branches)
    {
        var search = new HybridSearchService(store, new CountingEmbeddingService(),
            NullLogger<HybridSearchService>.Instance);
        return search.SearchAsync(
            new SearchRequest(Query, 10, null, new AccessScope([], GrantsAccess: true, [.. branches])),
            TestContext.Current.CancellationToken);
    }

    // ADR-0036 read 規則の 3 節。分岐名はポリシー名（`AbacEvaluator` が付ける）に相当する。
    private static AccessScopeBranch OwnerBranch(string user) =>
        new("所有者ベース", [new AttributeFilter("owner", [user])]);

    private static AccessScopeBranch SharedBranch(string user) =>
        new("共有先ベース", [new AttributeFilter(AttributeValueKeys.SharedWith, [user])]);

    private static AccessScopeBranch ConfidentialityBranch(params string[] levels) =>
        new("静的属性ベース", [new AttributeFilter(ConfidentialityLevels.AttributeKey, [.. levels])]);

    // 受け入れ基準 3（前半）: 所有者本人には見える。
    [Fact]
    public async Task 所有者は自分の個人資料を横断検索で見つけられる()
    {
        var results = await SearchAsync(StoreWith(AliceNote(), OrganizationDoc()),
            OwnerBranch("alice"), ConfidentialityBranch(ConfidentialityLevels.Restricted));

        results.Should().Contain(r => r.DocumentTitle == "alice の個人資料");
    }

    // 🔴 受け入れ基準 3（後半）: **共有先に含まれない他者は、restricted クリアランスを持っていても
    // 見えない。** これが `ADR-0061` 決定 6 が名指しした事故の形である。
    //
    // 陽性対照: 同じスコープで**組織文書（restricted）は見えている** ——
    // 「クリアランスの分岐がそもそも効いていないから 0 件」ではないことを示す。
    [Fact]
    public async Task 共有されていない他者には見えない_同じスコープで組織文書は見える()
    {
        var results = await SearchAsync(StoreWith(AliceNote(), OrganizationDoc()),
            OwnerBranch("mallory"),
            SharedBranch("mallory"),
            ConfidentialityBranch(ConfidentialityLevels.Restricted));

        results.Should().Contain(r => r.DocumentTitle == "組織の個人資料ではない文書",
            "陽性対照: 静的属性ベースの分岐は効いている（結果が空なだけの緑にしない）");
        results.Should().NotContain(r => r.DocumentTitle == "alice の個人資料",
            "所有者でも共有先でもない主体には、クリアランスが足りていても見えない");
    }

    // 🔴 受け入れ基準 4: **所有者が明示的に共有した相手には見える。**
    // **肯定テストで置く**（issue の指定）—— `owner` 単独判定に退行すると、ここだけが落ちる。
    [Fact]
    public async Task 共有された相手は横断検索で見つけられる()
    {
        var results = await SearchAsync(StoreWith(AliceNote()), SharedBranch("bob"));

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("alice の個人資料");
    }

    // 🔴 受け入れ基準 8: **`confidentiality` しか見ない分岐では個人資料を許可しない**（決定 6）。
    //
    // 陽性対照 1: 同じ分岐で**組織文書（restricted）は見える** —— 分岐そのものは効いている。
    // 陽性対照 2: 同じ資料が**裁量の分岐（所有者）では見える** —— 索引に在り、語も当たっている。
    // この 2 つが無いと、本テストは「何も返らない実装」でも緑になる。
    [Fact]
    public async Task 機密区分だけの分岐は個人資料を許可しない()
    {
        var store = StoreWith(AliceNote(), OrganizationDoc());

        var byClearance = await SearchAsync(store,
            ConfidentialityBranch(ConfidentialityLevels.Restricted));
        var byOwner = await SearchAsync(store, OwnerBranch("alice"));

        byClearance.Should().Contain(r => r.DocumentTitle == "組織の個人資料ではない文書",
            "陽性対照 1: 静的属性ベースの分岐は効いている");
        byClearance.Should().NotContain(r => r.DocumentTitle == "alice の個人資料",
            "🔴 `confidentiality` だけで判定してはならない（ADR-0061 決定 6）");
        byOwner.Should().Contain(r => r.DocumentTitle == "alice の個人資料",
            "陽性対照 2: 同じ資料が裁量の分岐では見える（索引に在ることの確認）");
    }

    // 決定 6 を**述語の側でも**固定する。分岐の裁量性の判定が裏返ると、
    // 上のテストは「分岐が何も許可しない」形でも緑になり得る。
    [Fact]
    public void 裁量の分岐は所有者か共有先を条件に持つものだけである()
    {
        PrivateNoteVisibility.IsDiscretionaryBranch(
            [new AttributeFilter("owner", ["alice"])]).Should().BeTrue();
        PrivateNoteVisibility.IsDiscretionaryBranch(
            [new AttributeFilter(AttributeValueKeys.SharedWith, ["bob"])]).Should().BeTrue();
        PrivateNoteVisibility.IsDiscretionaryBranch(
            [new AttributeFilter(ConfidentialityLevels.AttributeKey, ["restricted"])])
            .Should().BeFalse();
        PrivateNoteVisibility.IsDiscretionaryBranch([]).Should().BeFalse(
            "条件の無い分岐（そのポリシーの範囲で全件許可）に個人資料を含めない");
    }

    // 決定 3: **索引は用途で分けない。**「ナレッジグラフに表示」だけが ON の個人資料は
    // 索引には載るが、**横断検索の結果には出ない**。
    //
    // 陽性対照: 同じ所有者の「横断検索 ON」の資料は出ている。
    [Fact]
    public async Task グラフ用途だけで索引に載った個人資料は横断検索に出ない()
    {
        var graphOnly = Chunk("グラフ用途だけの個人資料", null,
            (DocumentScopes.Key, DocumentScopes.PrivateNote),
            ("owner", "alice"),
            (DocumentExposure.SearchKey, DocumentExposure.Excluded),
            (DocumentExposure.GraphKey, DocumentExposure.Included),
            (DocumentExposure.AiKey, DocumentExposure.Excluded));

        var results = await SearchAsync(StoreWith(graphOnly, AliceNote()), OwnerBranch("alice"));

        results.Should().Contain(r => r.DocumentTitle == "alice の個人資料",
            "陽性対照: 横断検索 ON の資料は出る");
        results.Should().NotContain(r => r.DocumentTitle == "グラフ用途だけの個人資料",
            "用途の別は文書属性で表す（ADR-0061 決定 3）——索引に在ることと検索に出ることは別である");
    }

    // 露出の投影を持たない**組織文書**は従来どおり出る（遡及付与しない方針を壊していない）。
    // 上の陰性 2 件が「個人資料だから」であって「露出キーが無いから」ではないことを分ける対照。
    [Fact]
    public async Task 露出キーを持たない組織文書は従来どおり検索に出る()
    {
        var results = await SearchAsync(StoreWith(OrganizationDoc()),
            ConfidentialityBranch(ConfidentialityLevels.Restricted));

        results.Should().ContainSingle();
    }
}
