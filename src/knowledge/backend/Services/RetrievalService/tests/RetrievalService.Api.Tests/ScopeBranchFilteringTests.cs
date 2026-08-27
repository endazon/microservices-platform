using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Composable.Adapters;
using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Tests;

// FR-05, FR-19, UC-01, ADR-0036, ADR-0046 D-06, IADR-0253 決定 1（段 3・検索側の分岐対応 / #989）:
// **認可スコープの選言（名前つき分岐）で検索が絞られること**を固定する。
//
// 計画の read 規則は「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の**選言**であり、
// 単一の連言（AllowedFilters）では表せない。段 1・2 が契約と評価器へ分岐を入れ、
// 本段で消費側（検索）が**分岐間 OR・分岐内 AND**で評価する。
//
// 🔴 **本試験群の要は「混成の拒否」である**（IADR-0253 決定 2 の追記が実証した反例）。
// キー単位 union へ畳むと、**どのポリシー単独も許可しない値の組合せ**を許してしまう。
public class ScopeBranchFilteringTests
{
    private static ChunkPayload Chunk(string title, params (string Key, string Value)[] attrs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, $"{title} の本文", [0.1f], null,
            attrs.ToDictionary(a => a.Key, a => a.Value), []);

    private static InMemoryVectorStore StoreWith(params ChunkPayload[] chunks)
    {
        var store = new InMemoryVectorStore();
        foreach (var c in chunks) store.UpsertAsync(c).GetAwaiter().GetResult();
        return store;
    }

    // IADR-0253 決定 2 の反例そのもの: ポリシー A と B が別々の値で同じ 2 キーを条件づける。
    private static readonly AccessScopeBranch PolicyA = new("A: 人事の内部資料",
        [new AttributeFilter("confidentiality", ["internal"]), new AttributeFilter("department", ["hr"])]);

    private static readonly AccessScopeBranch PolicyB = new("B: 営業の公開資料",
        [new AttributeFilter("confidentiality", ["public"]), new AttributeFilter("department", ["sales"])]);

    private static ScopeFilter Branches(params AccessScopeBranch[] branches) =>
        new([], [.. branches.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)]);

    // ── 正例: 分岐間 OR ────────────────────────────────────────────────────────
    // A だけを満たす文書と B だけを満たす文書が**両方**返る。
    [Fact]
    public async Task Search_EvaluatesBranchesAsDisjunction_BothPoliciesAreVisible()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("営業公開", ("confidentiality", "public"), ("department", "sales")));

        var results = await store.SearchAsync([0.1f], 10, Branches(PolicyA, PolicyB),
            TestContext.Current.CancellationToken);

        results.Select(r => r.DocumentTitle).Should().BeEquivalentTo(["人事内部", "営業公開"],
            "分岐どうしは OR である——どちらか 1 つの分岐を満たせば可視になる");
    }

    // ── 🔴 負例（本試験群の要）: 混成の拒否 ──────────────────────────────────────
    // (internal, sales) は A 単独でも B 単独でも許可されない。
    // キー単位 union（confidentiality ∈ {internal,public} AND department ∈ {hr,sales}）は
    // これを**許してしまう**。分岐評価は拒否しなければならない。
    [Fact]
    public async Task Search_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion()
    {
        var store = StoreWith(
            Chunk("混成", ("confidentiality", "internal"), ("department", "sales")));

        var results = await store.SearchAsync([0.1f], 10, Branches(PolicyA, PolicyB),
            TestContext.Current.CancellationToken);

        results.Should().BeEmpty(
            "(internal, sales) はどちらのポリシー単独でも許可されない。"
            + "キー単位 union へ畳むとこの混成が通ってしまう（IADR-0253 決定 2 の反例）");
    }

    // ── 陽性対照: 各分岐単独 ───────────────────────────────────────────────────
    // 「常に空」の実装を落とすため、1 分岐だけを与えて正しく通ることを確かめる。
    [Fact]
    public async Task Search_WithSingleBranch_MatchesOnlyThatPolicy()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("営業公開", ("confidentiality", "public"), ("department", "sales")));

        var onlyA = await store.SearchAsync([0.1f], 10, Branches(PolicyA),
            TestContext.Current.CancellationToken);
        onlyA.Should().ContainSingle().Which.DocumentTitle.Should().Be("人事内部");

        var onlyB = await store.SearchAsync([0.1f], 10, Branches(PolicyB),
            TestContext.Current.CancellationToken);
        onlyB.Should().ContainSingle().Which.DocumentTitle.Should().Be("営業公開");
    }

    // ── 属性キーの欠落は不一致（安全側） ───────────────────────────────────────
    [Fact]
    public async Task Search_DocumentMissingBranchAttribute_IsNotVisible()
    {
        var store = StoreWith(Chunk("部門なし", ("confidentiality", "internal")));

        var results = await store.SearchAsync([0.1f], 10, Branches(PolicyA),
            TestContext.Current.CancellationToken);

        results.Should().BeEmpty("属性キーを持たない文書は不一致（欠落は deny 側へ倒す）");
    }

    // ── 文書条件を持たない分岐 = そのポリシーの範囲で全件許可 ──────────────────
    [Fact]
    public async Task Search_BranchWithNoFilters_GrantsAll()
    {
        var store = StoreWith(
            Chunk("何か", ("confidentiality", "secret"), ("department", "legal")));

        var results = await store.SearchAsync([0.1f], 10,
            Branches(new AccessScopeBranch("無条件許可", [])),
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle("文書条件の無いポリシーはその範囲で全件を許可する");
    }

    // ── 後方互換: 分岐なしは従来どおり連言で評価 ───────────────────────────────
    [Fact]
    public async Task Search_WithoutBranches_FallsBackToConjunction()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("営業公開", ("confidentiality", "public"), ("department", "sales")));

        var results = await store.SearchAsync([0.1f], 10,
            new ScopeFilter([new AttributeFilter("department", ["hr"])]),
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("人事内部");
    }

    // ── 連言（利用者指定の絞り込み）は分岐の選言と AND で重なる ────────────────
    // 絞り込みは narrowing であり権限を広げない。
    [Fact]
    public async Task Search_ConjunctionNarrowsTheDisjunction()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("営業公開", ("confidentiality", "public"), ("department", "sales")));

        var filters = new ScopeFilter(
            [new AttributeFilter("department", ["sales"])],
            [PolicyA.Filters, PolicyB.Filters]);

        var results = await store.SearchAsync([0.1f], 10, filters,
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.DocumentTitle.Should().Be("営業公開",
            "利用者の絞り込みは分岐の選言全体と AND で重なる（権限は広がらない）");
    }

    // ── キーワード検索側にも同じ規則が効く ─────────────────────────────────────
    [Fact]
    public async Task KeywordSearch_AppliesTheSameBranchRules()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("混成", ("confidentiality", "internal"), ("department", "sales")));

        var results = await store.KeywordSearchAsync("本文", 10, Branches(PolicyA, PolicyB),
            TestContext.Current.CancellationToken);

        results.Select(r => r.DocumentTitle).Should().BeEquivalentTo(["人事内部"],
            "全文検索側でも混成は拒否される（系統によって認可が変わってはならない）");
    }

    // ── 値集合の照会（/attribute-values）にも同じ規則が効く ────────────────────
    // ADR-0043 / IADR-0151 決定 1: 候補と検索が食い違うと「候補に出るのに絞れない」が生まれる。
    [Fact]
    public async Task ListAttributeValues_AppliesTheSameBranchRules()
    {
        var store = StoreWith(
            Chunk("人事内部", ("confidentiality", "internal"), ("department", "hr")),
            Chunk("混成", ("confidentiality", "internal"), ("department", "sales")));

        var values = await store.ListAttributeValuesAsync("attributes.department",
            Branches(PolicyA, PolicyB), TestContext.Current.CancellationToken);

        values.Should().BeEquivalentTo(["hr"],
            "混成の文書は到達できないので、その値は候補にも出ない");
    }

    // ── Qdrant への写像: 分岐は Should（OR）＋入れ子 Must（AND）になる ──────────
    // 実機 Qdrant 無しで固定できる唯一の面である（BuildAttributeConditions と同じ理由）。
    [Fact]
    public void QdrantMapping_BranchesBecomeNestedShouldOfMust()
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions(Branches(PolicyA, PolicyB));

        conditions.Should().ContainSingle("分岐の選言は 1 つの入れ子条件へ畳まれる");
        var should = conditions[0].Filter.Should;
        should.Should().HaveCount(2, "分岐 2 本が Should（OR）で並ぶ");
        should.Should().AllSatisfy(c => c.Filter.Must.Should().HaveCount(2,
            "各分岐の中はキー条件が Must（AND）で並ぶ"));
    }

    // 🔴 陰性対照: 全件許可の分岐があれば選言そのものが消える（制約にならない）。
    [Fact]
    public void QdrantMapping_BranchWithNoFilters_DropsTheDisjunction()
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions(
            Branches(PolicyA, new AccessScopeBranch("無条件許可", [])));

        conditions.Should().BeEmpty(
            "文書条件を持たない分岐は「その範囲で全件許可」であり、選言は制約にならない");
    }

    // 回帰: 分岐が無ければ従来どおりキーごとの条件が並ぶ（写像を変えていない）。
    [Fact]
    public void QdrantMapping_WithoutBranches_IsUnchanged()
    {
        var conditions = QdrantVectorStore.BuildAttributeConditions(
            new ScopeFilter([new AttributeFilter("department", ["hr"])]));

        conditions.Should().ContainSingle();
        conditions[0].Field.Key.Should().Be("attributes.department");
    }
}
