using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-17, ADR-0035, #969: **文書 ID 集合に絞った意味検索**（二段検索の後段）を固定する。
//
// グラフ近傍展開が返すのは**文書単位**の候補であり、出典（`CitationDto`）が要る
// `ChunkId` / `Score` / `Snippet` を持たない。その文書 ID に絞って検索を走らせることで
// チャンク単位のスコアとスニペットを得る、というのが ADR-0035 決定 1 の二段検索である。
//
// 固定するのは 3 点である。
//   1. 文書 ID 集合の**外**は返らない（否定形 ＋ 陽性対照）
//   2. ABAC フィルタと **AND** で重なる（文書 ID の制約は ABAC を置き換えない）
//   3. **空集合は「該当なし」**であって「全件」ではない（グラフが 0 件を返したときに全文書へ広がらない）
[Trait("TestKind", "Unit")]
public class VectorStoreDocumentScopedSearchTests
{
    private static readonly Guid DocA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ChunkPayload Chunk(
        Guid documentId, string title, string department, float[] vector) =>
        new(Guid.NewGuid(), documentId, title, $"{title} の本文", vector, null,
            new Dictionary<string, string> { ["department"] = department }, []);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    // FR-04, FR-17: 文書 ID 集合の**中**だけが返る。集合外の文書は、権限があっても候補にならない
    // （グラフが出した候補文書をチャンクへ展開する、という二段目の役目そのもの）。
    [Fact]
    public async Task SearchWithinDocuments_ReturnsOnlyChunksOfListedDocuments()
    {
        var store = StoreWith(
            Chunk(DocA, "グラフが出した文書", "finance", [1f, 0f]),
            Chunk(DocB, "無関係な文書", "finance", [1f, 0f]));

        var results = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [DocA], null, TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentId.Should().Be(DocA);
    }

    // FR-04, FR-05, FR-17: **ABAC と AND で重なる。** 文書 ID で絞るのは追加の制約であって、
    // ABAC を置き換えるものではない —— グラフが出した文書でも権限外なら返らない。
    // 陽性対照（同じ文書・同じ集合で、許可される部門なら返る）を並べ、
    // 「そもそも何も返らない」実装で緑にならないようにする。
    [Fact]
    public async Task SearchWithinDocuments_AndsDocumentScopeWithAbacFilters()
    {
        var store = StoreWith(Chunk(DocA, "法務の文書", "legal", [1f, 0f]));

        var denied = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [DocA],
            new ScopeFilter([new AttributeFilter("department", ["finance"])]), TestContext.Current.CancellationToken);

        denied.Should().BeEmpty("文書 ID 集合に入っていても ABAC が拒否する属性なら返らない");

        // 陽性対照: ABAC が許可する属性なら、同じ文書 ID 集合で返る。
        var allowed = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [DocA],
            new ScopeFilter([new AttributeFilter("department", ["legal"])]), TestContext.Current.CancellationToken);

        allowed.Should().ContainSingle().Which.DocumentId.Should().Be(DocA);
    }

    // FR-04, FR-17: 🔴 **空集合は「該当なし」。** 空を「全件」と読むと、グラフが 0 件を返したときに
    // 検索が全文書へ広がる（＝二段検索が事実上ただの全体検索になる）。
    // 陽性対照つき —— 同じストア・同じ問い合わせで、非空の集合なら返ることを併せて示す。
    [Fact]
    public async Task SearchWithinDocuments_EmptyDocumentSet_ReturnsNothing()
    {
        var store = StoreWith(
            Chunk(DocA, "文書 A", "finance", [1f, 0f]),
            Chunk(DocB, "文書 B", "finance", [1f, 0f]));

        var empty = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [], null, TestContext.Current.CancellationToken);

        empty.Should().BeEmpty("空集合は「全件」ではなく「該当なし」である");

        // 陽性対照: 集合が空でなければ返る（ストアが空だから 0 件、ではない）。
        var nonEmpty = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [DocA, DocB], null, TestContext.Current.CancellationToken);

        nonEmpty.Should().HaveCount(2);
    }

    // FR-04, FR-17, #995: 🔴 **本口は `queryVector` を実際に見る。**
    // 既存の `InMemoryVectorStore.SearchAsync` は `queryVector` を参照せずスコア `0.9f` を返すため、
    // ベクトル側の欠陥がテストで緑のまま通り抜ける（#995 で実際に起きた）。
    // 二段検索の後段はチャンクの `Score` をそのまま出典へ載せるので、
    // ここで「見ているふり」をすると順位の検証が空振りする。
    [Fact]
    public async Task SearchWithinDocuments_ScoresByQueryVector()
    {
        var store = StoreWith(
            Chunk(DocA, "問いに近いチャンク", "finance", [1f, 0f]),
            Chunk(DocA, "問いに直交するチャンク", "finance", [0f, 1f]));

        var results = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [DocA], null, TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        results[0].DocumentTitle.Should().Be("問いに近いチャンク");
        results[0].Score.Should().BeApproximately(1f, 1e-5f);
        results[1].Score.Should().BeApproximately(0f, 1e-5f, "直交するベクトルは似ていない");
    }

    // FR-04, FR-05, FR-17, #969: Qdrant 側のフィルタ組み立て。
    // **`document_id ∈ {ids}`（`Match.Keywords`）と ABAC 条件を `Must`（AND）で並べる**ことを、
    // 実機 Qdrant なしで固定する（Docker が使えない環境でも押さえられる唯一の面。
    // `BuildAttributeConditions` を `internal` にしてあるのと同じ理由）。
    [Fact]
    public void BuildDocumentScopedFilter_AndsDocumentIdsWithAbacConditions()
    {
        var filter = QdrantVectorStore.BuildDocumentScopedFilter(
            [DocA, DocB], new ScopeFilter([new AttributeFilter("department", ["legal"])]));

        filter.Must.Should().HaveCount(2, "文書 ID の条件と ABAC 条件が AND で並ぶ");

        var documentCondition = filter.Must[0].Field;
        documentCondition.Key.Should().Be(QdrantVectorStore.DocumentIdKey);
        documentCondition.Match.Keywords.Strings.Should()
            .BeEquivalentTo([DocA.ToString(), DocB.ToString()]);

        var abacCondition = filter.Must[1].Field;
        abacCondition.Key.Should().Be(AttributeValueKeys.ToPayloadKey("department"));
        abacCondition.Match.Keywords.Strings.Should().BeEquivalentTo(["legal"]);
    }

    // FR-04, FR-17, #969: Qdrant 実装でも **空集合は「該当なし」**であり、
    // **クライアントを呼ぶ前に**返る。空の `Keywords` を投げて Qdrant 側の解釈に委ねない
    // （実装差で「全件」に化けうる）。
    //
    // 到達できない宛先のクライアントを渡してあるので、**呼べば例外になる** ——
    // 例外なく空が返ることが「呼んでいない」ことの証拠である。
    [Fact]
    public async Task SearchWithinDocuments_EmptyDocumentSet_DoesNotQueryQdrant()
    {
        var store = new QdrantVectorStore(
            new QdrantClient("127.0.0.1", 65500),
            new ConfigurationBuilder().Build(),
            NullLogger<QdrantVectorStore>.Instance,
            TestMeterFactory.NewKeywordSearchMetrics());

        var results = await store.SearchWithinDocumentsAsync(
            [1f, 0f], 10, [], null, TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }
}
